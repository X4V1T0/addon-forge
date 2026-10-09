using System.Text.RegularExpressions;
using AddonForge.Core.Analysis;

namespace AddonForge.Core.Generation;

/// <summary>
/// Home Assistant only ever writes an addon's configured options to
/// /data/options.json inside the container — confirmed against the official
/// docs: config.yaml's `environment:` key sets *static* values only, it does
/// not support `{{ options.KEY }}` templating (that only exists in HA's own
/// automations/scripts, not addon config.yaml). A packaged third-party app
/// has no idea /data/options.json exists; it just expects plain env vars.
/// This generates a small POSIX-sh wrapper that translates one into the
/// other before handing off to the app's own original entrypoint, so
/// addon-forge never has to touch the app's own code.
///
/// Delivering that (and rendering a config file template, if any) needs to
/// read /data/options.json, which Home Assistant's Supervisor always writes
/// with mode 600 (confirmed directly against its own source:
/// `jsonfile.chmod(0o600)` in supervisor/utils/json.py) - unreadable by
/// whatever non-root user a third-party Dockerfile happens to declare. The
/// obvious fix - temporarily become root, then drop back to that user before
/// handing off - turns out to actively break a real, if less common, pattern
/// some apps use: a forked child (e.g. a cron job) writing to its own
/// parent's `/proc/1/fd/1` to make its output show up in `docker logs`.
/// Confirmed directly, repeatedly, and the hard way: *every* userspace
/// privilege-drop tool tried (su, setpriv, chpst, gosu) leaves the dropped
/// process non-dumpable (a Linux kernel security measure, triggered by the
/// credential transition itself, not a quirk of any one tool) - and a
/// non-dumpable process's `/proc/PID/fd/*` can't be opened by anyone else at
/// all, even a same-UID child, without CAP_SYS_PTRACE (which a non-root
/// container process never has). So instead of ever changing this script's
/// own privilege level, jq itself is made setuid-root (confirmed to work
/// correctly, including on distroless) and used to read options.json (and
/// render a config file template) - the *file it reads* needs root, not the
/// process asking it to. Nothing in this script or the app it hands off to
/// ever changes UID, so there's nothing to taint.
/// </summary>
public static partial class EntrypointWrapperGenerator
{
    public const string ScriptFileName = "addon-forge-entrypoint.sh";
    public const string ConfigTemplateFileName = "addon-forge-config.template";

    /// <summary>
    /// A tiny (empty) marker file, COPYed in purely to force-create the
    /// config file's own destination directory with permissions a non-root
    /// container can actually use - see BuildDockerfileAppendix's own doc
    /// comment for why this needs to exist at all.
    /// </summary>
    public const string SeedFileName = "addon-forge-seed";

    /// <summary>
    /// The ignore files BuildKit reads for our generated root Dockerfile: a
    /// Dockerfile-specific one wins over the plain one when both exist.
    /// </summary>
    public static readonly string[] DockerignoreFileNames = ["Dockerfile.dockerignore", ".dockerignore"];

    /// <summary>
    /// Re-includes every file addon-forge itself COPYs in (all named
    /// `addon-forge-*`) at the end of a repo's own ignore file. Allowlist-style
    /// ignores (`*` then `!src/`...) are common and would otherwise drop them
    /// from the build context - "addon-forge-entrypoint.sh: not found" at
    /// build time. Appended last since the last matching pattern wins.
    /// </summary>
    public static string AllowGeneratedFilesInDockerignore(string dockerignoreContent)
    {
        var separator = dockerignoreContent.Length == 0 || dockerignoreContent.EndsWith('\n') ? "" : "\n";
        return dockerignoreContent + separator +
               "\n# --- addon-forge: keep its generated build inputs in the context ---\n" +
               "!addon-forge-*\n";
    }

    /// <summary>
    /// jq's own official image - `FROM scratch`, a single statically-linked
    /// binary at /jq, published for every architecture Home Assistant runs
    /// on. Verified directly (not assumed): `docker run --rm ghcr.io/jqlang/jq:latest
    /// --version` -> jq-1.8.2, `docker image inspect` confirms `Entrypoint: [/jq]`
    /// with no other layers, and `docker manifest inspect` lists amd64/arm64/arm
    /// among its platforms. Copied in via `COPY --from`, which needs no shell
    /// and no package manager - unlike installing a jq package, this works
    /// identically regardless of the target image's own base or active user.
    /// </summary>
    private const string JqImageRef = "ghcr.io/jqlang/jq:latest";

    private const string CopiedJqPath = "/addon-forge-jq";

    // Skips any leading `--platform=...`-style flags before capturing the
    // actual image reference (e.g. `FROM --platform=$BUILDPLATFORM golang:1.27-bookworm`
    // must not mistake the flag itself for the image name).
    [GeneratedRegex(@"^\s*FROM\s+(?:--\S+\s+)*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FromLine();

    [GeneratedRegex(@"^\s*(ENTRYPOINT|CMD)\s+(.+?)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex EntrypointOrCmdLine();

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^[ \t]*VOLUME[ \t]+(.+?)[ \t\r]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VolumeLine();

    /// <summary>
    /// Every distinct `${OPTION_NAME}` referenced in a config file template
    /// (see ManagedAddon.ConfigFileTemplate), in first-seen order. Used to
    /// automatically create an option for any placeholder that doesn't
    /// already correspond to one - otherwise, a name used only in the
    /// template (and never separately detected or added as an extra env var)
    /// would silently never show up in the addon's own Configuration tab,
    /// with nothing around to tell it apart from a typo.
    /// </summary>
    public static IReadOnlyList<string> ExtractPlaceholderNames(string configFileTemplate) =>
        [.. PlaceholderPattern().Matches(configFileTemplate)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// True when the final stage's base image is known to have no shell at
    /// all — literally `scratch`, or a distroless image not already on its
    /// "debug" variant (see <see cref="ApplyDistrolessDebugVariant"/>). A
    /// script-based wrapper can never run there, and would break the build
    /// outright: without a shell, even our own appended `COPY`/`ENTRYPOINT`
    /// lines have nothing to interpret the script with at runtime. Callers
    /// should skip wrapping (and warn) rather than generate an addon that
    /// fails to build/start.
    /// </summary>
    public static bool FinalStageHasNoShell(string dockerfileContent)
    {
        var finalImage = FinalStageImage(dockerfileContent);
        if (finalImage is null)
        {
            return false;
        }

        if (finalImage.Contains("scratch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Distroless's own "debug" tag variant ships a minimal busybox shell
        // specifically so a build step like this one can run - any other tag
        // has none.
        return IsDistrolessImage(finalImage) && !finalImage.Contains("debug", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True once the final stage is a distroless image on its "debug" tag
    /// (whether it started that way or <see cref="ApplyDistrolessDebugVariant"/>
    /// just rewrote it there). Distroless's debug variant ships busybox at
    /// `/busybox/*` (confirmed against the distroless project's own build
    /// config: `entrypoint = ["/busybox/sh"]`, `env PATH += /busybox`) -
    /// callers need this to know the shell to invoke is `/busybox/sh`, not
    /// `/bin/sh`, which doesn't exist there.
    /// </summary>
    public static bool IsDistrolessDebugFinalStage(string dockerfileContent)
    {
        var finalImage = FinalStageImage(dockerfileContent);
        return finalImage is not null &&
               IsDistrolessImage(finalImage) &&
               finalImage.Contains("debug", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// If the final stage's base image is a distroless image not already on
    /// a "debug" tag, rewrites just that image reference to the matching
    /// debug variant (distroless's own busybox-shell build, made exactly for
    /// this) - `nonroot` becomes `debug-nonroot`, anything else becomes
    /// `debug`. An explicit, opt-in trade-off (see ManagedAddon.AllowDistrolessDebugShell):
    /// it removes some of the hardening (no shell, no package manager) the
    /// original image shipped with. Returns the content unchanged if the
    /// final stage isn't a rewritable distroless reference (not distroless
    /// at all, already on a debug tag, or pinned by digest - `@sha256:...` -
    /// where guessing an equivalent debug digest isn't safe).
    /// </summary>
    public static string ApplyDistrolessDebugVariant(string dockerfileContent)
    {
        var matches = FromLine().Matches(dockerfileContent);
        if (matches.Count == 0)
        {
            return dockerfileContent;
        }

        var imageGroup = matches[^1].Groups[1];
        var imageRef = imageGroup.Value;

        if (imageRef.Contains('@') ||
            !IsDistrolessImage(imageRef) ||
            imageRef.Contains("debug", StringComparison.OrdinalIgnoreCase))
        {
            return dockerfileContent;
        }

        var colonIndex = imageRef.LastIndexOf(':');
        var repo = colonIndex >= 0 ? imageRef[..colonIndex] : imageRef;
        var tag = colonIndex >= 0 ? imageRef[(colonIndex + 1)..] : "";
        var debugTag = tag.Contains("nonroot", StringComparison.OrdinalIgnoreCase) ? "debug-nonroot" : "debug";

        return dockerfileContent[..imageGroup.Index] + $"{repo}:{debugTag}" + dockerfileContent[(imageGroup.Index + imageGroup.Length)..];
    }

    private static string? FinalStageImage(string dockerfileContent)
    {
        var matches = FromLine().Matches(dockerfileContent);
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value;
    }

    /// <summary>"distroless" covers the whole gcr.io/distroless/* family (static, base, cc, ...).</summary>
    private static bool IsDistrolessImage(string imageRef) =>
        imageRef.Contains("distroless", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the wrapper script content, or null if no ENTRYPOINT/CMD could
    /// be found to preserve (nothing safe to wrap around). When
    /// <paramref name="configFileDestPath"/> is given, the script also
    /// renders <see cref="ConfigTemplateFileName"/> (a file the app reads
    /// that only exposes some settings outside env vars — see
    /// ManagedAddon.ConfigFileTemplate) to that path before handing off.
    /// <paramref name="useDistrolessDebugTools"/> only changes which shell
    /// the script itself is interpreted by (`/busybox/sh` vs `/bin/sh`) -
    /// see the type-level doc comment for why nothing here ever needs to
    /// change *which user* it runs as. <paramref name="sharedPaths"/> uses the
    /// same symlink mechanism as <paramref name="persistentPaths"/>, but
    /// targets `/share/&lt;slug&gt;/...` instead of `/data/persist/...` - for
    /// handing a path to a *different* addon (see PersistenceDetector.SharedTargetFor),
    /// not for surviving a rebuild. <paramref name="slug"/> is required
    /// whenever <paramref name="sharedPaths"/> is non-empty.
    /// </summary>
    public static string? BuildWrapperScript(
        string dockerfileContent,
        string? configFileDestPath = null,
        bool useDistrolessDebugTools = false,
        IReadOnlyList<string>? persistentPaths = null,
        IReadOnlyList<string>? sharedPaths = null,
        string? slug = null)
    {
        if (sharedPaths is { Count: > 0 } && string.IsNullOrWhiteSpace(slug))
        {
            throw new ArgumentException("slug is required when sharedPaths is non-empty.", nameof(slug));
        }

        var shellPath = useDistrolessDebugTools ? "/busybox/sh" : "/bin/sh";

        var entrypoint = FindLastInstruction(dockerfileContent, "ENTRYPOINT");
        var cmd = FindLastInstruction(dockerfileContent, "CMD");

        var execLine = BuildExecLine(entrypoint, cmd, shellPath);
        if (execLine is null)
        {
            return null;
        }

        var optionsSection =
            "if [ -f /data/options.json ]; then\n" +
            $"  eval \"$({CopiedJqPath} -r 'to_entries[] | \"export \" + .key + \"=\" + (.value | tostring | @sh)' /data/options.json)\"\n" +
            "fi\n";

        var configFileSection = string.IsNullOrWhiteSpace(configFileDestPath)
            ? ""
            : "\n" + BuildConfigFileSection(configFileDestPath);

        var symlinks = (persistentPaths ?? []).Select(p => (Source: p, Target: PersistenceDetector.TargetFor(p)))
            .Concat((sharedPaths ?? []).Select(p => (Source: p, Target: PersistenceDetector.SharedTargetFor(slug!, p))))
            .ToList();

        return $"#!{shellPath}\n" +
               "# Generated by addon-forge. Home Assistant writes this addon's configured\n" +
               "# options to /data/options.json but never injects them as environment\n" +
               "# variables on its own - the packaged app only understands plain env vars,\n" +
               "# so this translates one into the other before handing off to it.\n" +
               "set -e\n" +
               "\n" +
               (symlinks.Count > 0 ? BuildPersistenceSection(symlinks) + "\n" : "") +
               optionsSection +
               configFileSection +
               "\n" +
               execLine + "\n";
    }

    /// <summary>
    /// Home Assistant keeps exactly one directory per addon across a rebuild:
    /// /data. An app that hardcodes its state path elsewhere (and ignores any
    /// env var meant to move it) silently loses everything on every update.
    /// Since the path can't be changed from outside, this makes it a symlink
    /// into /data instead - the app keeps writing where it always did, and the
    /// filesystem redirects it. The first time, the image's own copy of the
    /// directory (defaults, permissions) is moved over; after that /data
    /// wins and the image's copy is discarded, so an update never overwrites
    /// real data. Needs root to replace the original directory, so it's
    /// skipped (loudly) for an image that runs as a non-root user - /data
    /// isn't writable by one anyway. The same mechanism (and the same root
    /// requirement) covers shared paths too - the shell function itself
    /// doesn't care which root a target lives under, only the caller does
    /// (see BuildWrapperScript). The target is chowned to whoever owned the
    /// image's directory: files dropped there over Samba belong to root, and
    /// an app that drops privileges can't fix that itself - `chown -R` on the
    /// symlink it now sees doesn't follow it. For the same reason, anything
    /// the app's own root phase creates in there afterwards (`mkdir -p
    /// $DIR/plugins` before dropping privileges) would stay root's: the
    /// target's directories are made setgid and the app starts under umask
    /// 002, so such a directory inherits the original group, group-writable.
    /// </summary>
    private static string BuildPersistenceSection(IReadOnlyList<(string Source, string Target)> symlinks)
    {
        var calls = string.Concat(symlinks.Select(s =>
            $"  addon_forge_persist {ShellQuote(s.Source)} {ShellQuote(s.Target)}\n"));

        return
            "if [ \"$(id -u)\" = \"0\" ]; then\n" +
            "  addon_forge_persist() {\n" +
            "    if [ -L \"$1\" ]; then\n" +
            "      mkdir -p \"$2\"\n" +
            "      return 0\n" +
            "    fi\n" +
            "    if [ -e \"$1\" ] && [ ! -d \"$1\" ]; then\n" +
            "      echo \"addon-forge: $1 is not a directory, leaving it alone\" >&2\n" +
            "      return 0\n" +
            "    fi\n" +
            "    owner=\"\"\n" +
            "    if [ -d \"$1\" ]; then\n" +
            "      owner=\"$(stat -c '%u:%g' \"$1\" 2>/dev/null || true)\"\n" +
            "    fi\n" +
            "    if [ ! -e \"$2\" ]; then\n" +
            "      mkdir -p \"$(dirname \"$2\")\"\n" +
            "      if [ -d \"$1\" ]; then\n" +
            "        cp -a \"$1\" \"$2\" || mkdir -p \"$2\"\n" +
            "      else\n" +
            "        mkdir -p \"$2\"\n" +
            "      fi\n" +
            "    fi\n" +
            "    rm -rf \"$1\" 2>/dev/null || true\n" +
            "    if [ -e \"$1\" ]; then\n" +
            "      echo \"addon-forge: could not persist $1 (it is still in place - a mount point?)\" >&2\n" +
            "      return 0\n" +
            "    fi\n" +
            "    mkdir -p \"$(dirname \"$1\")\"\n" +
            "    ln -s \"$2\" \"$1\"\n" +
            "    if [ -n \"$owner\" ] && [ \"$owner\" != \"0:0\" ]; then\n" +
            "      chown -R \"$owner\" \"$2\" || true\n" +
            "      find \"$2\" -type d -exec chmod g+ws {} + 2>/dev/null || true\n" +
            "      addon_forge_group_writable=1\n" +
            "    fi\n" +
            "  }\n" +
            calls +
            "  if [ -n \"${addon_forge_group_writable:-}\" ]; then\n" +
            "    umask 0002\n" +
            "  fi\n" +
            "else\n" +
            "  echo \"addon-forge: not running as root, cannot persist data paths outside /data\" >&2\n" +
            "fi\n";
    }

    /// <summary>
    /// A `VOLUME` at a path that is being symlinked away (persisted into
    /// /data, or shared into /share) has to go: Docker mounts an anonymous
    /// volume there at runtime, and a mount point can't be replaced by a
    /// symlink (nor would that volume survive an update anyway). Removes
    /// just those paths from each VOLUME line, or comments the whole line
    /// out if nothing else is left on it. Callers pass the union of
    /// persistent and shared paths - both get symlinked away the same way.
    /// </summary>
    public static string StripVolumeDeclarations(string dockerfileContent, IReadOnlyList<string> symlinkedPaths)
    {
        if (symlinkedPaths.Count == 0)
        {
            return dockerfileContent;
        }

        return VolumeLine().Replace(dockerfileContent, m =>
        {
            var raw = m.Groups[1].Value.Trim();
            var tokens = raw.StartsWith('[')
                ? Regex.Matches(raw, "\"([^\"]+)\"").Select(t => t.Groups[1].Value).ToList()
                : raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"')).ToList();

            var kept = tokens
                .Where(t => !symlinkedPaths.Any(p => t.TrimEnd('/') == p || t.StartsWith(p + "/", StringComparison.Ordinal)))
                .ToList();

            if (kept.Count == tokens.Count)
            {
                return m.Value;
            }

            return kept.Count == 0
                ? $"# addon-forge: removed (symlinked elsewhere instead): VOLUME {raw}"
                : "VOLUME [" + string.Join(", ", kept.Select(k => "\"" + k + "\"")) + "]";
        });
    }

    /// <summary>
    /// jq's own `gsub` does plain, literal string replacement - it does not
    /// re-interpret `\(...)`-style interpolation syntax that happens to
    /// appear inside the *values* being substituted in (verified directly:
    /// a value containing a literal `\(danger)` sequence comes through
    /// unevaluated, same as one already sitting in the template). That
    /// matters here because those values are whatever the user configured
    /// in Home Assistant's Configuration tab - untrusted input as far as
    /// this script is concerned.
    ///
    /// Every step here can fail without the setuid-jq trick saving it -
    /// unlike reading /data/options.json, *writing* configFileDestPath is
    /// done by this shell's own (possibly non-root) fd, jq's setuid bit only
    /// elevates jq itself once exec'd, after the destination is already
    /// opened (or has failed to open). AddonConfigGenerator/BuildDockerfileAppendix
    /// cover the common case by pre-seeding that destination world-writable
    /// at build time, but the fallback here has to keep working without that
    /// too (e.g. a destination outside the addon's own generated Dockerfile
    /// entirely). The fallback is `cat template > dest`, deliberately never
    /// `cp template dest`: confirmed directly, `cp` unlinks and recreates its
    /// destination internally rather than truncating the existing inode in
    /// place, which needs *directory* write permission - exactly what a
    /// pre-seeded destination file doesn't grant. A shell redirect only ever
    /// needs write permission on the file itself. Confirmed directly against
    /// a real build: example-app's own distroless `nonroot` image can't
    /// create new entries anywhere under `/` at all, and its own config.yaml
    /// is documented as optional (it logs "Not using config.yaml" and
    /// carries on with just its env vars/defaults if it's missing) - so a
    /// failed write here must never be fatal either way. The final fallback
    /// (an `echo ... >&2`) always succeeds, so this whole chain's exit
    /// status is always 0 regardless of which step actually worked, and
    /// `set -e` never aborts the script over it.
    /// </summary>
    private static string BuildConfigFileSection(string configFileDestPath) =>
        $"if [ -f /{ConfigTemplateFileName} ]; then\n" +
        $"  mkdir -p \"$(dirname {ShellQuote(configFileDestPath)})\" 2>/dev/null || true\n" +
        $"  {CopiedJqPath} -r --rawfile tmpl /{ConfigTemplateFileName} " +
        "'reduce (to_entries[]) as $e ($tmpl; gsub(\"\\\\$\\\\{\" + $e.key + \"\\\\}\"; ($e.value | tostring)))' " +
        $"/data/options.json > {ShellQuote(configFileDestPath)} 2>/dev/null || " +
        $"cat /{ConfigTemplateFileName} > {ShellQuote(configFileDestPath)} 2>/dev/null || " +
        $"echo \"addon-forge: could not write config file at {configFileDestPath} (no write permission there?) - continuing without it\" >&2\n" +
        "fi\n";

    /// <summary>
    /// Appended after the source repo's own (unmodified) Dockerfile content.
    /// Since it comes after everything else, it applies to the final build
    /// stage — exactly where the addon's actual ENTRYPOINT needs to end up,
    /// regardless of how many earlier build stages the Dockerfile has.
    /// COPY-only, no RUN, no USER switch at all - see the type-level doc
    /// comment for why: the wrapper script's executable bit comes from
    /// `COPY --chmod=755`, and jq (made setuid-root via `COPY --chmod=4755`
    /// from its own official image) reads whatever it needs to regardless of
    /// which user is actually running this container.
    ///
    /// <paramref name="configFileDestPath"/> gets the exact same treatment
    /// for a completely different reason: writing it isn't a read, so jq's
    /// setuid bit doesn't help there at all (confirmed directly: `jq ... >
    /// dest` fails under a non-root image regardless of jq's own privilege -
    /// the shell opens `dest` itself, before jq is even exec'd). Instead,
    /// the destination file - and, if it doesn't already exist, its
    /// directory - is COPYed in here with a world-writable mode
    /// (`--chmod`, same build-time-only mechanism as jq's setuid bit, which
    /// also ignores whatever USER is active). At runtime the wrapper then
    /// only ever *opens an existing* world-writable file instead of trying
    /// to *create* one in a directory it can't write to - confirmed directly
    /// against example-app's own `gcr.io/distroless/static-debian13:debug-nonroot`:
    /// this is what actually let its config.yaml get written as uid 65532.
    /// (A tried-and-rejected alternative, for the record: a second setuid
    /// helper binary to perform the write itself, the same way jq performs
    /// the read - but busybox's own `cp`/`mkdir` applets deliberately *drop*
    /// their setuid privilege before doing any file I/O, confirmed directly
    /// - `busybox id` reports the caller's real uid, not 0, even when the
    /// busybox binary itself is `-rwsr-xr-x root root`. This build-time
    /// pre-seed approach sidesteps that entirely - nothing at runtime needs
    /// elevated privilege of its own at all.)
    /// </summary>
    public static string BuildDockerfileAppendix(bool includeConfigTemplate = false, string? configFileDestPath = null)
    {
        var configCopy = includeConfigTemplate ? $"COPY {ConfigTemplateFileName} /{ConfigTemplateFileName}\n" : "";

        var seedCopy = "";
        if (includeConfigTemplate && !string.IsNullOrWhiteSpace(configFileDestPath))
        {
            // A single `--chmod` on a COPY that also has to create the
            // destination's parent directory applies that same mode to the
            // directory too - and a directory with no execute bit can't be
            // traversed at all, not even by root (confirmed directly). So
            // the directory is force-created with a separate, dedicated
            // COPY first (777 - needs the execute bit to be usable as a
            // directory at all) before the real destination file's own COPY
            // (666 - a config file has no business being executable).
            // Skipped harmlessly if the directory already existed in the
            // original image, since a second COPY into an existing
            // directory never touches that directory's own mode.
            var destDir = PosixDirname(configFileDestPath);
            var markerPath = destDir == "/" ? "/.addon-forge-keep" : $"{destDir}/.addon-forge-keep";
            seedCopy =
                $"COPY --chmod=777 {SeedFileName} {markerPath}\n" +
                $"COPY --chmod=666 {ConfigTemplateFileName} {configFileDestPath}\n";
        }

        return "\n" +
               "# --- addon-forge: inject Home Assistant addon options as env vars ---\n" +
               $"COPY --chmod=755 {ScriptFileName} /addon-forge-entrypoint.sh\n" +
               $"COPY --from={JqImageRef} --chmod=4755 /jq {CopiedJqPath}\n" +
               configCopy +
               seedCopy +
               "ENTRYPOINT [\"/addon-forge-entrypoint.sh\"]\n";
    }

    /// <summary>POSIX `dirname` semantics for a container path we already know is absolute (e.g. "/config.yaml" -&gt; "/", "/config/settings.yaml" -&gt; "/config").</summary>
    private static string PosixDirname(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }

    private static string? BuildExecLine(Instruction? entrypoint, Instruction? cmd, string shellPath)
    {
        if (entrypoint is { IsExecForm: true })
        {
            var args = new List<string>(entrypoint.Args);
            if (cmd is { IsExecForm: true })
            {
                args.AddRange(cmd.Args);
            }

            return "exec " + string.Join(' ', args.Select(ShellQuote));
        }

        if (entrypoint is { IsExecForm: false })
        {
            return $"exec {shellPath} -c {ShellQuote(entrypoint.Args[0])}";
        }

        if (cmd is { IsExecForm: true })
        {
            return "exec " + string.Join(' ', cmd.Args.Select(ShellQuote));
        }

        if (cmd is { IsExecForm: false })
        {
            return $"exec {shellPath} -c {ShellQuote(cmd.Args[0])}";
        }

        return null;
    }

    private static Instruction? FindLastInstruction(string dockerfileContent, string keyword)
    {
        Instruction? last = null;

        foreach (Match m in EntrypointOrCmdLine().Matches(dockerfileContent))
        {
            if (!string.Equals(m.Groups[1].Value, keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = m.Groups[2].Value.Trim();
            last = raw.StartsWith('[')
                ? new Instruction([.. ExtractJsonArrayStrings(raw)], IsExecForm: true)
                : new Instruction([raw], IsExecForm: false);
        }

        return last;
    }

    private static IEnumerable<string> ExtractJsonArrayStrings(string raw) =>
        Regex.Matches(raw, "\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(m => m.Groups[1].Value.Replace("\\\"", "\""));

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private sealed record Instruction(IReadOnlyList<string> Args, bool IsExecForm);
}
