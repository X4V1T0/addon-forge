using AddonForge.Core.Analysis;
using AddonForge.Core.Models;

namespace AddonForge.Core.Generation;

/// <summary>
/// Assembles the full set of files for a generated Home Assistant addon.
/// The source repo's own Dockerfile is copied through unchanged except for
/// one addition when there are options to deliver: an appended entrypoint
/// wrapper (see EntrypointWrapperGenerator) that translates HA's
/// /data/options.json into real env vars before handing off to the app's own
/// original entrypoint. Supervisor builds the image directly from what's
/// written here — addon-forge never builds or pulls images itself, with one
/// exception: a SourceKind.DockerImage addon that needs no wrapping at all
/// (see <see cref="AddonGenerationRequest.Image"/>) gets a plain `image:` in
/// config.yaml instead of a Dockerfile, and the Supervisor pulls it directly.
/// </summary>
public static class AddonConfigGenerator
{
    public static GeneratedAddonFiles Generate(RepoAnalysisResult analysis, AddonGenerationRequest request)
    {
        var (options, schema) = SchemaBuilder.Build(analysis.EnvVars);

        var persistentPaths = PersistenceDetector.NormalizePaths(request.PersistentPaths);
        var sharedPaths = PersistenceDetector.NormalizePaths(request.SharedPaths, allowUnderData: true);

        var overlap = persistentPaths.Intersect(sharedPaths, StringComparer.Ordinal).ToList();
        if (overlap.Count > 0)
        {
            throw new InvalidOperationException(
                $"'{string.Join("', '", overlap)}' cannot be both a persistent path and a shared path - " +
                "pick one (a shared path is still readable by this addon itself afterwards).");
        }

        // A published image's own `VOLUME` is part of its metadata, inherited
        // by anything built FROM it - a Dockerfile on top can re-declare or
        // ignore it but can never remove it (confirmed directly against
        // example/geodata: still listed under Config.Volumes after
        // dropping its VOLUME line). Docker therefore mounts a volume at that
        // path when the container starts, and a mount point can't be replaced
        // by a symlink (`rm -rf` -> "Resource busy"). Persisting or sharing it
        // could only ever fail at runtime with one line in the addon's log, so
        // fail here instead, where it can be explained. (A git-sourced
        // Dockerfile's own VOLUME lines are a different story: those ARE
        // stripped - see StripVolumeDeclarations.)
        if (!string.IsNullOrWhiteSpace(request.Image))
        {
            var inheritedVolumes = analysis.Volumes.Select(v => v.ContainerPath.TrimEnd('/')).ToList();
            var blocked = persistentPaths.Concat(sharedPaths)
                .Where(p => inheritedVolumes.Any(v => v == p || v.StartsWith(p + "/", StringComparison.Ordinal)))
                .ToList();

            if (blocked.Count > 0)
            {
                throw new InvalidOperationException(
                    $"'{string.Join("', '", blocked)}' is declared as a VOLUME by the published image itself. " +
                    "A VOLUME inherited from an image can't be removed by anything built on top of it, so Docker " +
                    "mounts a volume there when the container starts, and a mount point can't be replaced by a " +
                    "symlink. Point the app at a different directory instead (most apps have a setting or " +
                    "environment variable for where they keep their data) and persist or share that one.");
            }
        }

        var hasConfigFile = !string.IsNullOrWhiteSpace(request.ConfigFileTemplate) &&
                             !string.IsNullOrWhiteSpace(request.ConfigFileDestPath);

        // Manual ports fill in what nothing could be auto-detected for - most
        // images built around `network_mode: host` (what HostNetwork below
        // maps to) don't declare EXPOSE/ExposedPorts at all, since they were
        // never meant to need Docker's own port publishing. Manual wins on a
        // conflict with whatever WAS auto-detected, same convention as
        // ManualEnvVars filling gaps a Dockerfile/compose file left undetected.
        var (manualPorts, portsDescription) = ManualPortParser.ParseAll(request.ManualPorts);
        var ports = MergePorts(analysis.Ports, manualPorts);

        // Whether *anything* needs delivering into the running container that
        // only a build step (appending the entrypoint wrapper) can deliver.
        // Only when this is false, and the request came with a published
        // image to begin with, is there nothing left for a build to achieve -
        // see the native `image:` branch below.
        var needsWrapping = options.Count > 0 || hasConfigFile || persistentPaths.Count > 0 || sharedPaths.Count > 0;

        var dockerfileContent = request.DockerfileContent;

        // Opt-in only (see ManagedAddon.AllowDistrolessDebugShell) - switches
        // a distroless final stage to its own "debug" tag, which ships a
        // minimal shell for exactly this purpose. A no-op if the final stage
        // isn't a rewritable distroless reference.
        var debugVariantApplied = false;
        if (request.AllowDistrolessDebugShell && !string.IsNullOrWhiteSpace(dockerfileContent))
        {
            var rewritten = EntrypointWrapperGenerator.ApplyDistrolessDebugVariant(dockerfileContent);
            debugVariantApplied = rewritten != dockerfileContent;
            dockerfileContent = rewritten;
        }

        string? wrapperScript = null;
        var noShellWithOptions = false;
        var useDistrolessDebugTools = false;

        if (!string.IsNullOrWhiteSpace(dockerfileContent) && needsWrapping)
        {
            if (EntrypointWrapperGenerator.FinalStageHasNoShell(dockerfileContent))
            {
                // No shell exists there at all - a script-based wrapper can
                // never run (confirmed against a real build: even our own
                // appended RUN/ENTRYPOINT lines fail on a shell-less base
                // image like scratch or distroless). Ship the addon anyway
                // (better than failing outright) but say so in DOCS.md rather
                // than silently leaving options (and any config file) undelivered.
                noShellWithOptions = true;
            }
            else
            {
                // Only changes which shell the wrapper script itself is
                // interpreted by (`/busybox/sh` on distroless's debug tag,
                // since it has no `/bin/sh`) - see EntrypointWrapperGenerator's
                // own doc comment for why nothing here needs a privileged
                // setup step or a package manager at all, on any base image.
                useDistrolessDebugTools = EntrypointWrapperGenerator.IsDistrolessDebugFinalStage(dockerfileContent);

                wrapperScript = EntrypointWrapperGenerator.BuildWrapperScript(
                    dockerfileContent, hasConfigFile ? request.ConfigFileDestPath : null, useDistrolessDebugTools,
                    persistentPaths, sharedPaths, request.Slug);
                if (wrapperScript is not null)
                {
                    dockerfileContent = EntrypointWrapperGenerator.StripVolumeDeclarations(
                                            dockerfileContent, [.. persistentPaths, .. sharedPaths]) +
                                        EntrypointWrapperGenerator.BuildDockerfileAppendix(
                                            hasConfigFile, hasConfigFile ? request.ConfigFileDestPath : null);
                }
            }
        }

        // Nothing a build could have delivered ended up needed, and this
        // request came with a published image to begin with - the Supervisor
        // can just pull it directly, same as any Home Assistant addon that
        // was never meant to be built locally at all.
        var useNativeImage = !string.IsNullOrWhiteSpace(request.Image) && !needsWrapping;

        var manifest = new AddonManifest
        {
            Name = request.Name,
            Version = request.Version,
            Slug = request.Slug,
            Description = request.Description,
            Url = request.SourceWebUrl,
            Ingress = request.Ingress,
            IngressPort = request.Ingress ? request.IngressPort : null,
            HostNetwork = request.HostNetwork,
            Ports = request.Ingress ? [] : BuildPorts(ports),
            PortsDescription = request.Ingress ? [] : portsDescription,
            Map = BuildMapEntries(request.MapEntries, needsShareWrite: sharedPaths.Count > 0),
            Options = options,
            Schema = schema,
            Image = useNativeImage ? request.Image : null,
            Arch = request.Arch is { Count: > 0 } ? [.. request.Arch] : ["amd64"],
        };

        var files = new Dictionary<string, string>
        {
            ["config.yaml"] = YamlSerialization.ToYaml(manifest),
            ["DOCS.md"] = DocsGenerator.BuildDocs(
                request, analysis, schema, wrapperScript is not null, noShellWithOptions, debugVariantApplied,
                persistentPaths, sharedPaths),
            ["CHANGELOG.md"] = DocsGenerator.BuildChangelogEntry(request),
        };

        if (!useNativeImage && !string.IsNullOrWhiteSpace(dockerfileContent))
        {
            files["Dockerfile"] = dockerfileContent;
        }

        if (wrapperScript is not null)
        {
            files[EntrypointWrapperGenerator.ScriptFileName] = wrapperScript;

            if (hasConfigFile)
            {
                files[EntrypointWrapperGenerator.ConfigTemplateFileName] = request.ConfigFileTemplate!;
                // Empty - only ever COPYed in to force-create the config
                // file's destination directory with a usable mode. See
                // BuildDockerfileAppendix's own doc comment for why this
                // has to exist at all.
                files[EntrypointWrapperGenerator.SeedFileName] = "";
            }
        }

        return new GeneratedAddonFiles
        {
            Slug = request.Slug,
            Version = request.Version,
            Ref = request.Ref,
            Files = files,
        };
    }

    /// <summary>
    /// Passes the user's own `map:` entries through as-is, except: a shared
    /// path (see ManagedAddon.SharedPaths) needs `share:rw` to actually write
    /// into /share, and the user shouldn't have to remember that on top of
    /// listing the paths themselves. Any existing `share:` entry (say, a
    /// read-only one added for some other reason) is replaced rather than
    /// left alongside it - Home Assistant's Supervisor rejects a `map:` with
    /// the same target listed twice.
    /// </summary>
    private static List<string> BuildMapEntries(IReadOnlyList<string> requested, bool needsShareWrite)
    {
        var result = requested.ToList();
        if (needsShareWrite)
        {
            result.RemoveAll(e => e.StartsWith("share:", StringComparison.OrdinalIgnoreCase));
            result.Add("share:rw");
        }

        return result;
    }

    /// <summary>Manual ports win on a conflict (same container port + protocol) with whatever was auto-detected.</summary>
    private static List<PortMapping> MergePorts(IReadOnlyList<PortMapping> detected, IReadOnlyList<PortMapping> manual)
    {
        var byKey = detected.ToDictionary(p => (p.ContainerPort, p.Protocol));
        foreach (var p in manual)
        {
            byKey[(p.ContainerPort, p.Protocol)] = p;
        }

        return [.. byKey.Values.OrderBy(p => p.ContainerPort)];
    }

    private static Dictionary<string, int?> BuildPorts(IReadOnlyList<PortMapping> ports)
    {
        var result = new Dictionary<string, int?>();
        foreach (var p in ports)
        {
            var key = $"{p.ContainerPort}/{(p.Protocol == PortProtocol.Udp ? "udp" : "tcp")}";
            result[key] = p.SuggestedHostPort ?? p.ContainerPort;
        }

        return result;
    }
}
