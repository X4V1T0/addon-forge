namespace AddonForge.Core.Models;

/// <summary>
/// What addon-forge builds a managed addon from. GitRepo is the original (and
/// still default) path: clone a source repo and build its own Dockerfile.
/// DockerImage skips git and the build entirely - ManagedAddon.SourceRepoUrl
/// instead holds an image reference (e.g. "docker.io/example/agent")
/// that's inspected directly against its registry (see
/// IContainerRegistryClient) for ports/volumes/env/entrypoint, the same way
/// RepoAnalyzer reads a Dockerfile - just from the already-built image's own
/// config instead of source. See AddonGenerationOrchestrator for how the two
/// converge on the same generation pipeline.
/// </summary>
public enum SourceKind
{
    GitRepo = 0,

    DockerImage = 1,
}
