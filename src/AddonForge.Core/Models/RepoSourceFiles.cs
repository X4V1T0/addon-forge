namespace AddonForge.Core.Models;

/// <summary>
/// Raw text content of the files RepoAnalyzer cares about, read from a cloned
/// repo checkout. Reading from disk happens in Infrastructure; this is a plain
/// data holder so Core.Analysis stays pure and testable without touching a
/// filesystem.
/// </summary>
public class RepoSourceFiles
{
    public string? DockerfileContent { get; init; }

    /// <summary>Repo-relative path <see cref="DockerfileContent"/> was read from; null means the root Dockerfile.</summary>
    public string? DockerfilePath { get; init; }

    public string? ComposeContent { get; init; }

    public string? EnvExampleContent { get; init; }

    public string? ReadmeContent { get; init; }
}
