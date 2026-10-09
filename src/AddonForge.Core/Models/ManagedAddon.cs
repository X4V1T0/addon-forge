namespace AddonForge.Core.Models;

/// <summary>
/// State addon-forge persists about an addon it generated and is watching for
/// upstream updates. This is our own bookkeeping — it is not the Home Assistant
/// addon's config.yaml, though the two are kept in sync by the generator.
/// </summary>
public class ManagedAddon
{
    public required string Slug { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// GitRepo (default): a git repo URL, cloned and built from its own
    /// Dockerfile - see <see cref="DockerfilePath"/>. DockerImage: a
    /// published image reference with no tag (e.g.
    /// "docker.io/example/agent"), inspected directly against its
    /// registry instead - see <see cref="SourceRepoUrl"/>'s own doc comment
    /// for what it holds in that case, and IContainerRegistryClient.
    /// </summary>
    public SourceKind SourceKind { get; set; } = SourceKind.GitRepo;

    /// <summary>
    /// A git repo URL for SourceKind.GitRepo (the original, and still
    /// default, meaning). For SourceKind.DockerImage, this instead holds the
    /// image reference with no tag (e.g. "docker.io/example/agent")
    /// - <see cref="CurrentRef"/>/<see cref="StagedRef"/> hold the tag
    /// actually tracked, the same way they hold a git tag/commit SHA for a
    /// git-sourced addon. Reusing this one field (rather than adding a
    /// parallel "ImageReference" property) keeps every other place that
    /// already threads SourceRepoUrl through - <see cref="ManualEnvVars"/>,
    /// <see cref="MapEntries"/>, the update/regenerate flow - working
    /// unchanged for both source kinds.
    /// </summary>
    public required string SourceRepoUrl { get; set; }

    /// <summary>Description written into the generated config.yaml, reused on every regeneration.</summary>
    public required string Description { get; set; }

    public bool Ingress { get; set; }

    public int? IngressPort { get; set; }

    /// <summary>
    /// Runs this addon on the host's own network instead of a Docker-managed
    /// one - see AddonManifest.HostNetwork for exactly what that changes.
    /// Required by images built around `network_mode: host` (e.g. some reverse proxies).
    /// </summary>
    public bool HostNetwork { get; set; }

    /// <summary>
    /// Manually declared ports (see Analysis.ManualPortParser for the exact
    /// syntax, one per line) - for an image that declares no
    /// EXPOSE/ExposedPorts at all (common for one built around
    /// `network_mode: host`), which addon-forge has nothing to detect on its
    /// own. Merged with whatever ports WERE auto-detected (this wins on a
    /// conflict), the same way <see cref="ManualEnvVars"/> fills gaps a
    /// Dockerfile/compose file left undetected. Preserved across every future
    /// regeneration like <see cref="MapEntries"/>.
    /// </summary>
    public List<string> ManualPorts { get; set; } = [];

    /// <summary>
    /// Repo-relative path to a non-root Dockerfile to use, for repos with no
    /// Dockerfile at their root (multi-service stacks often ship one per
    /// component instead). Null means "use the root Dockerfile" as normal.
    /// </summary>
    public string? DockerfilePath { get; set; }

    /// <summary>
    /// Manually chosen HA `map:` entries (e.g. "media:rw") — addon-forge has
    /// no reliable way to guess on its own which host folder a container
    /// path like /music is supposed to be, so this is the user's call, made
    /// once and then preserved across every future regeneration rather than
    /// getting silently dropped each time config.yaml is rewritten.
    /// </summary>
    public List<string> MapEntries { get; set; } = [];

    /// <summary>
    /// Manually added env vars, one per line as `KEY=value` or bare `KEY`
    /// (same convention as a .env.example) — for anything the repo only
    /// documents in prose (README, wiki) rather than in its Dockerfile,
    /// compose file or .env.example, which addon-forge has no way to
    /// discover on its own. Preserved across every future regeneration the
    /// same way <see cref="MapEntries"/> is.
    /// </summary>
    public List<string> ManualEnvVars { get; set; } = [];

    /// <summary>
    /// Content of a config file the app reads that only exposes some
    /// settings through a file, not env vars (e.g. a dashboard app's weather
    /// settings) — with `${OPTION_NAME}` placeholders referencing this
    /// addon's own options. Rendered by simple text substitution, not
    /// parsed, so any text format works (YAML/JSON/TOML/INI/...). Null if
    /// this addon has none. Preserved across every future regeneration the
    /// same way <see cref="MapEntries"/> is.
    /// </summary>
    public string? ConfigFileTemplate { get; set; }

    /// <summary>Absolute path inside the container to write the rendered <see cref="ConfigFileTemplate"/> to. Required together with it.</summary>
    public string? ConfigFileDestPath { get; set; }

    /// <summary>
    /// Opt-in: if this addon's final build stage is a distroless image (no
    /// shell, no package manager - so options/a config file can't be
    /// delivered there), switch it to distroless's own "debug" tag variant,
    /// which ships a minimal shell for exactly that purpose. Off by default
    /// because it removes hardening the upstream image deliberately shipped
    /// with - an explicit, per-addon choice, never automatic.
    /// </summary>
    public bool AllowDistrolessDebugShell { get; set; }

    /// <summary>
    /// Container paths kept across rebuilds by symlinking them into /data
    /// (the one directory Home Assistant preserves per addon) - for apps
    /// that hardcode a state path and ignore any env var to move it.
    /// Preserved across every future regeneration like <see cref="MapEntries"/>.
    /// </summary>
    public List<string> PersistentPaths { get; set; } = [];

    /// <summary>
    /// Container paths symlinked into /share/&lt;slug&gt;/... instead of
    /// /data - not to survive this addon's own rebuild (its own /data
    /// already does that), but so a *different* addon can read them through
    /// its own `share:ro` map entry (e.g. a bouncer/monitoring addon reading
    /// this one's log files, which it otherwise has no way to see inside
    /// this addon's private /data). Automatically adds `share:rw` to this
    /// addon's own `map:` - see AddonConfigGenerator.BuildMapEntries. Purely
    /// manual, the same way <see cref="PersistentPaths"/> is: nothing is
    /// detected on this addon's own repo alone, since which path is worth
    /// exposing (and to whom) is the user's call. Preserved across every
    /// future regeneration like <see cref="MapEntries"/>.
    /// </summary>
    public List<string> SharedPaths { get; set; } = [];

    /// <summary>What the last analysis found that the app probably keeps state in (see PersistenceDetector) - stored so the UI can warn without re-cloning the repo.</summary>
    public List<PersistentPathCandidate> DetectedPersistentPaths { get; set; } = [];

    /// <summary>When <see cref="DetectedPersistentPaths"/> was last computed; null for an addon created before detection existed, which triggers a one-time backfill on its next check.</summary>
    public DateTimeOffset? PersistenceCheckedAt { get; set; }

    /// <summary>True once the user has looked at and saved the persistent-paths choice (even an empty one), which silences the data-at-risk warning.</summary>
    public bool PersistenceReviewed { get; set; }

    /// <summary>Strongly-detected state paths that aren't persisted and that nobody has reviewed yet - data an update would silently lose.</summary>
    public IReadOnlyList<PersistentPathCandidate> DataAtRisk =>
        PersistenceReviewed
            ? []
            : [.. DetectedPersistentPaths.Where(c => c.Recommended && !PersistentPaths.Contains(c.Path))];

    public SourceRefType SourceRefType { get; set; } = SourceRefType.Tag;

    /// <summary>Branch name to track when <see cref="SourceRefType"/> is Branch. Ignored otherwise.</summary>
    public string? TrackedBranch { get; set; }

    /// <summary>Ref (tag name or commit SHA) currently written to the local addon store.</summary>
    public string? CurrentRef { get; set; }

    /// <summary>Addon version currently written to the local addon store (config.yaml `version`).</summary>
    public string? CurrentVersion { get; set; }

    public UpdatePolicy UpdatePolicy { get; set; } = UpdatePolicy.Manual;

    public DateTimeOffset? LastCheckedAt { get; set; }

    public DateTimeOffset? LastWrittenAt { get; set; }

    /// <summary>Ref sitting in staging, waiting to be written (manual policy), if any.</summary>
    public string? StagedRef { get; set; }

    /// <summary>Version sitting in staging, waiting to be written (manual policy), if any.</summary>
    public string? StagedVersion { get; set; }

    public string? LastError { get; set; }

    public AddonStatus Status
    {
        get
        {
            if (!string.IsNullOrEmpty(LastError))
            {
                return AddonStatus.GenerationError;
            }

            if (!string.IsNullOrEmpty(StagedRef) && !string.Equals(StagedRef, CurrentRef, StringComparison.Ordinal))
            {
                return AddonStatus.UpdatePending;
            }

            return AddonStatus.UpToDate;
        }
    }
}
