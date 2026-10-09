namespace AddonForge.Core.Models;

/// <summary>
/// Everything RepoAnalyzer could determine, deterministically, about a source
/// repo: what to expose, what to persist, and what options it probably needs.
/// </summary>
public class RepoAnalysisResult
{
    public bool HasDockerfile { get; init; }

    public bool HasCompose { get; init; }

    public IReadOnlyList<PortMapping> Ports { get; init; } = [];

    public IReadOnlyList<VolumeMapping> Volumes { get; init; } = [];

    public IReadOnlyList<EnvVarCandidate> EnvVars { get; init; } = [];

    /// <summary>Paths this app likely keeps state in that Home Assistant would lose on a rebuild — see PersistenceDetector.</summary>
    public IReadOnlyList<PersistentPathCandidate> PersistentPathCandidates { get; init; } = [];

    /// <summary>
    /// Populated only when HasDockerfile is false: other Dockerfile-like
    /// files found elsewhere in the repo (relative paths), for repos that
    /// don't build from a single root Dockerfile — multi-service stacks
    /// often ship one Dockerfile per component instead.
    /// </summary>
    public IReadOnlyList<string> DockerfileCandidates { get; init; } = [];

    /// <summary>Env vars whose name/default gave no reliable signal — candidates for LLM assistance.</summary>
    public IReadOnlyList<EnvVarCandidate> AmbiguousEnvVars =>
        [.. EnvVars.Where(v => v.IsAmbiguous)];

    /// <summary>First non-empty heading/paragraph of the README, for use as a description seed.</summary>
    public string? ReadmeExcerpt { get; init; }
}
