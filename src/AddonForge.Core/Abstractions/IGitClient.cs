using AddonForge.Core.Models;

namespace AddonForge.Core.Abstractions;

/// <summary>A shallow checkout of a repo on local disk. Dispose to delete the temp directory.</summary>
public interface IClonedRepo : IAsyncDisposable
{
    string Path { get; }

    RepoSourceFiles ReadSourceFiles();

    /// <summary>
    /// Recursively copies the checkout (everything except .git) into
    /// destinationDirectory. The generated Dockerfile is rarely
    /// self-contained — it usually COPYs other files from the build context
    /// (requirements.txt, package.json, source dirs...) — so the addon needs
    /// the whole tree, not just the Dockerfile, or the Supervisor's build
    /// fails on the first COPY instruction.
    /// </summary>
    Task CopyToAsync(string destinationDirectory, CancellationToken ct);

    /// <summary>
    /// Finds Dockerfile-like files anywhere in the checkout (named
    /// "Dockerfile" or "Dockerfile.*", case-insensitive), excluding one at
    /// the root. Multi-service repos often have one per component instead of
    /// a single root Dockerfile — this lets the caller offer a pick when
    /// RepoSourceFiles.DockerfileContent came back null.
    /// </summary>
    IReadOnlyList<string> FindDockerfileCandidates();

    /// <summary>Reads one file's text content by a repo-relative path, or null if it doesn't exist.</summary>
    string? ReadFile(string relativePath);
}

public interface IGitClient
{
    /// <summary>Shallow-clones the given ref (tag, branch or SHA) of a repo to a temp directory.</summary>
    Task<IClonedRepo> ShallowCloneAsync(string repoUrl, string refName, CancellationToken ct);

    /// <summary>Latest semver-like tag/release, or null if the repo has none.</summary>
    Task<string?> GetLatestTagAsync(string repoUrl, CancellationToken ct);

    /// <summary>HEAD commit SHA of the given branch.</summary>
    Task<string> GetLatestCommitShaAsync(string repoUrl, string branch, CancellationToken ct);
}
