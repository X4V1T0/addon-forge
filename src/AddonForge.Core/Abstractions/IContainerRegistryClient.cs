using AddonForge.Core.Models;

namespace AddonForge.Core.Abstractions;

/// <summary>
/// Reads a published Docker/OCI image directly from its registry - no daemon,
/// no pull, just the plain HTTP Registry API (manifest + config blob) any
/// public registry (Docker Hub, ghcr.io, quay.io...) serves the same way.
/// This is SourceKind.DockerImage's equivalent of IGitClient: instead of
/// cloning a repo and reading its Dockerfile, it inspects an already-built
/// image's own config, which AddonForge.Core.Generation.ImageSourceGenerator
/// turns into the same ports/volumes/env shape a Dockerfile analysis would
/// have produced.
/// </summary>
public interface IContainerRegistryClient
{
    /// <summary>
    /// Inspects one concrete tag or digest - resolving a multi-arch manifest
    /// list down to its linux/amd64 entry first if that's what the tag points
    /// to (amd64 only for v1, same scope as the git-sourced build path).
    /// </summary>
    Task<ImageInspectionResult> InspectAsync(ImageReference reference, CancellationToken ct);

    /// <summary>Every tag this repository currently publishes - unfiltered, callers decide what counts as a real version.</summary>
    Task<IReadOnlyList<string>> ListTagsAsync(ImageReference reference, CancellationToken ct);

    /// <summary>
    /// The latest semver-ish tag (see Versioning.VersionComparer) - skips
    /// "latest", pre-releases, and anything that doesn't parse as a version
    /// at all. Null if none of the repository's tags parse as one.
    /// </summary>
    Task<string?> GetLatestTagAsync(ImageReference reference, CancellationToken ct);
}
