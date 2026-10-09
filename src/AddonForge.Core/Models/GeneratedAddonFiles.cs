namespace AddonForge.Core.Models;

/// <summary>
/// Output of the generator: file name (relative to the addon's folder) to
/// text content. Written to staging first, then copied to the local addon
/// store folder on publish.
/// </summary>
public class GeneratedAddonFiles
{
    public required string Slug { get; init; }

    public required string Version { get; init; }

    public required string Ref { get; init; }

    public Dictionary<string, string> Files { get; init; } = [];
}

/// <summary>Input to AddonConfigGenerator, gathered from the analysis step and user edits.</summary>
public class AddonGenerationRequest
{
    public required string Slug { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>
    /// The source's own identity - a real git URL for a GitRepo addon, or a
    /// bare image reference (e.g. "example/proxy", not a URL at all) for a
    /// DockerImage one. Shown as plain text/a link's *label*; never written
    /// directly to config.yaml's `url:` (see <see cref="SourceWebUrl"/>) -
    /// the Supervisor's own schema validates that field strictly as a URL,
    /// and a real addon-forge bug once wrote a bare image reference there,
    /// which made the Supervisor refuse to even load the generated config.yaml.
    /// </summary>
    public required string SourceRepoUrl { get; init; }

    /// <summary>
    /// A real, clickable http(s) URL for this addon's source, or null when
    /// none can be reliably constructed. For a GitRepo addon this is the same
    /// as <see cref="SourceRepoUrl"/>; for a DockerImage one, see
    /// AddonGenerationOrchestrator's own best-effort mapping (Docker
    /// Hub/ghcr.io only - anywhere else, null, which omits config.yaml's
    /// `url:` entirely rather than write something that might not resolve).
    /// </summary>
    public string? SourceWebUrl { get; init; }

    public required string Version { get; init; }

    public required string Ref { get; init; }

    public bool Ingress { get; init; }

    public int? IngressPort { get; init; }

    /// <summary>See ManagedAddon.HostNetwork.</summary>
    public bool HostNetwork { get; init; }

    /// <summary>See ManagedAddon.ManualPorts.</summary>
    public IReadOnlyList<string> ManualPorts { get; init; } = [];

    /// <summary>Raw Dockerfile content from the source repo, copied through as-is into the generated addon.</summary>
    public string? DockerfileContent { get; init; }

    /// <summary>Manually chosen HA `map:` entries (e.g. "media:rw") — see ManagedAddon.MapEntries.</summary>
    public IReadOnlyList<string> MapEntries { get; init; } = [];

    /// <summary>Optional summary (e.g. LLM-produced from the README/CHANGELOG) embedded into DOCS.md.</summary>
    public string? DocsSummary { get; init; }

    /// <summary>
    /// Content of a config file the app reads that only exposes some settings
    /// through a file, not env vars — with `${OPTION_NAME}` placeholders
    /// referencing this addon's own options (auto-detected or manual). Format
    /// agnostic (YAML/JSON/TOML/INI/...): rendered by simple text
    /// substitution (`envsubst`), never parsed. Null if this addon has none.
    /// </summary>
    public string? ConfigFileTemplate { get; init; }

    /// <summary>Absolute path inside the container to write the rendered <see cref="ConfigFileTemplate"/> to. Required together with it.</summary>
    public string? ConfigFileDestPath { get; init; }

    /// <summary>See ManagedAddon.AllowDistrolessDebugShell.</summary>
    public bool AllowDistrolessDebugShell { get; init; }

    /// <summary>See ManagedAddon.PersistentPaths.</summary>
    public IReadOnlyList<string> PersistentPaths { get; init; } = [];

    /// <summary>See ManagedAddon.SharedPaths.</summary>
    public IReadOnlyList<string> SharedPaths { get; init; } = [];

    /// <summary>
    /// Set only for a SourceKind.DockerImage addon: the full "repository:tag"
    /// reference to pull. When no wrapping ends up needed (no options, no
    /// config file, no persistent/shared paths), AddonConfigGenerator uses
    /// this as config.yaml's `image:` and skips writing a Dockerfile
    /// entirely - the Supervisor pulls the published image directly rather
    /// than building anything. When wrapping IS needed, this is ignored in
    /// favor of building <see cref="DockerfileContent"/> (a synthetic
    /// Dockerfile wrapped around the same image - see ImageSourceGenerator),
    /// the same as any other addon.
    /// </summary>
    public string? Image { get; init; }

    /// <summary>
    /// Overrides the default `["amd64"]` architecture list - only meaningful
    /// for a SourceKind.DockerImage addon, whose upstream image may already
    /// publish more architectures than addon-forge could ever build itself
    /// from a git-sourced Dockerfile. Null/empty keeps the amd64-only default.
    /// </summary>
    public IReadOnlyList<string>? Arch { get; init; }
}
