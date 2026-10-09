using AddonForge.Core.Models;

namespace AddonForge.Core.Analysis;

/// <summary>
/// Combines DockerfileParser and ComposeParser into one result. Compose
/// values win over Dockerfile values when both declare the same env var,
/// since compose is closer to "how this is actually run".
/// </summary>
public static class RepoAnalyzer
{
    public static RepoAnalysisResult Analyze(RepoSourceFiles files)
    {
        var dockerfilePorts = files.DockerfileContent is not null ? DockerfileParser.ParsePorts(files.DockerfileContent) : [];
        var dockerfileVolumes = files.DockerfileContent is not null ? DockerfileParser.ParseVolumes(files.DockerfileContent) : [];
        var dockerfileEnv = files.DockerfileContent is not null ? DockerfileParser.ParseEnvVars(files.DockerfileContent) : [];

        var compose = files.ComposeContent is not null
            ? ComposeParser.Parse(files.ComposeContent, files.DockerfilePath)
            : ComposeParseResult.Empty;
        var envExampleVars = files.EnvExampleContent is not null ? EnvExampleParser.Parse(files.EnvExampleContent) : [];

        var ports = MergePorts(dockerfilePorts, compose.Ports);
        var volumes = MergeVolumes(dockerfileVolumes, compose.Volumes);
        var envVars = MergeEnvVars(dockerfileEnv, compose.EnvVars, envExampleVars);

        return new RepoAnalysisResult
        {
            HasDockerfile = files.DockerfileContent is not null,
            HasCompose = files.ComposeContent is not null,
            Ports = ports,
            Volumes = volumes,
            PersistentPathCandidates = NoteConfigurableOptions(
                PersistenceDetector.Detect(files.DockerfileContent, compose.Volumes), envVars),
            EnvVars = envVars,
            ReadmeExcerpt = ExtractReadmeExcerpt(files.ReadmeContent),
        };
    }

    /// <summary>
    /// When an option's default IS the detected path (e.g. ND_MUSICFOLDER=/music),
    /// say so: the user may already point that option somewhere else, in which
    /// case the default path isn't used at all.
    /// </summary>
    private static List<PersistentPathCandidate> NoteConfigurableOptions(
        List<PersistentPathCandidate> candidates, List<EnvVarCandidate> envVars) =>
        [.. candidates.Select(c =>
        {
            var option = envVars.FirstOrDefault(v => v.DefaultValue?.TrimEnd('/') == c.Path);
            return option is null || c.Reason.Contains(option.Name)
                ? c
                : c with { Reason = $"{c.Reason} - also the default of option {option.Name}, which you can point elsewhere" };
        })];

    private static List<PortMapping> MergePorts(List<PortMapping> dockerfile, List<PortMapping> compose)
    {
        // Compose is authoritative when present (it reflects how the container is actually run);
        // Dockerfile EXPOSE only fills in ports compose didn't mention.
        var byPort = compose.ToDictionary(p => (p.ContainerPort, p.Protocol));
        foreach (var p in dockerfile)
        {
            byPort.TryAdd((p.ContainerPort, p.Protocol), p);
        }

        return [.. byPort.Values.Where(p => p.ContainerPort > 0).OrderBy(p => p.ContainerPort)];
    }

    private static List<VolumeMapping> MergeVolumes(List<VolumeMapping> dockerfile, List<VolumeMapping> compose)
    {
        var byPath = compose.ToDictionary(v => v.ContainerPath);
        foreach (var v in dockerfile)
        {
            byPath.TryAdd(v.ContainerPath, v);
        }

        return [.. byPath.Values.OrderBy(v => v.ContainerPath, StringComparer.Ordinal)];
    }

    private static List<EnvVarCandidate> MergeEnvVars(
        List<EnvVarCandidate> dockerfile, List<EnvVarCandidate> compose, List<EnvVarCandidate> envExample)
    {
        var byName = dockerfile.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var v in compose)
        {
            // compose wins on conflicts - it's closer to "what's actually run".
            byName[v.Name] = v;
        }

        foreach (var v in envExample)
        {
            // .env.example only fills gaps: containers that load their env via
            // `env_file:` rather than listing each key in compose leave
            // compose with nothing to see, but a Dockerfile/compose entry that
            // does exist is more specific to this container than a repo-wide
            // .env.example (which may also document other services' vars).
            byName.TryAdd(v.Name, v);
        }

        return [.. byName.Values.OrderBy(v => v.Name, StringComparer.Ordinal)];
    }

    private static string? ExtractReadmeExcerpt(string? readme)
    {
        if (string.IsNullOrWhiteSpace(readme))
        {
            return null;
        }

        // Most READMEs open with "# ProjectName" - using that verbatim as the
        // description is redundant with the addon's own Name field. Skip past
        // headings (remembering the first one only as a last-resort fallback)
        // and badge/image lines to find the first real descriptive sentence.
        string? fallbackHeading = null;

        foreach (var rawLine in readme.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var isHeading = trimmed.StartsWith('#');
            var text = isHeading ? trimmed.TrimStart('#').Trim() : trimmed;

            if (text.Length == 0 || text.StartsWith('!') || text.StartsWith('[') || text.StartsWith('<'))
            {
                continue;
            }

            if (isHeading)
            {
                fallbackHeading ??= text;
                continue;
            }

            return Truncate(text);
        }

        return fallbackHeading is not null ? Truncate(fallbackHeading) : null;
    }

    private static string Truncate(string text) => text.Length > 200 ? text[..200] : text;
}
