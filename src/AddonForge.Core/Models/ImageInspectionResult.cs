namespace AddonForge.Core.Models;

/// <summary>
/// What IContainerRegistryClient.InspectAsync reads directly out of a
/// published image's own manifest + config blob - the image-source
/// equivalent of RepoSourceFiles, just read from a registry instead of a git
/// checkout. ImageSourceGenerator turns this into the same shapes
/// RepoAnalyzer produces from a Dockerfile (ports/volumes/env), plus what's
/// needed to synthesize a minimal Dockerfile around the image itself
/// (entrypoint/cmd) - see AddonGenerationOrchestrator for how the two sources
/// converge on the same generation pipeline from there.
/// </summary>
public class ImageInspectionResult
{
    /// <summary>
    /// Digest of the image config blob actually inspected - a stable identity
    /// for exactly what was read, regardless of which tag or manifest-list
    /// entry pointed to it (unlike the tag itself, which can be re-pointed at
    /// new content later).
    /// </summary>
    public required string Digest { get; init; }

    /// <summary>
    /// Every architecture this image publishes a manifest for (from a
    /// manifest list/OCI index), or just the one manifest's own architecture
    /// if it isn't a list at all. Home Assistant's own `arch:` is set from
    /// this - unlike a git-sourced addon (always amd64-only for now, since
    /// Supervisor has to build it locally), an already-built multi-arch image
    /// costs nothing extra to offer on every architecture it actually publishes.
    /// </summary>
    public IReadOnlyList<string> Architectures { get; init; } = [];

    public IReadOnlyList<PortMapping> Ports { get; init; } = [];

    public IReadOnlyList<VolumeMapping> Volumes { get; init; } = [];

    /// <summary>
    /// Every "KEY=value" entry from the image's own baked-in Env - includes
    /// whatever every base layer set (PATH, toolchain versions...), not just
    /// what the app itself cares about. Deliberately raw; ImageSourceGenerator
    /// is what filters the noise out, so this stays a faithful, testable
    /// record of what the registry actually returned.
    /// </summary>
    public IReadOnlyList<string> RawEnv { get; init; } = [];

    /// <summary>Exec-form args exactly as the image's own config declares them (already resolved through every base image layer, unlike parsing Dockerfile text) - null if this image sets none.</summary>
    public IReadOnlyList<string>? Entrypoint { get; init; }

    /// <summary>Exec-form args, same as <see cref="Entrypoint"/> - null if this image sets none.</summary>
    public IReadOnlyList<string>? Cmd { get; init; }
}
