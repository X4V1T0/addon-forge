using AddonForge.Core.Abstractions;
using AddonForge.Core.Analysis;
using AddonForge.Core.Models;
using AddonForge.Core.Versioning;

namespace AddonForge.Core.Orchestration;

/// <summary>
/// Application-level operations on managed addons: create one from scratch,
/// check one for an upstream update (regenerating + staging, and publishing
/// too if its policy is Auto), and publish whatever is staged on demand. Used
/// by both the UI (on-demand actions) and the background scheduler (periodic
/// checks) so the two never drift apart.
/// </summary>
public class AddonUpdateService(
    IManagedAddonRepository repository,
    AddonGenerationOrchestrator generator,
    VersionCheckService versionChecker,
    IAddonFileWriter fileWriter,
    IGitClient gitClient)
{
    /// <summary>Generates, publishes directly (this is an explicit, one-off user action) and starts tracking a new addon.</summary>
    public async Task<ManagedAddon> CreateAsync(
        NewAddonInput input,
        UpdatePolicy policy,
        SourceRefType refType,
        string? trackedBranch,
        CancellationToken ct)
    {
        // For branch tracking, input.DisplayRef is just the branch name
        // (e.g. "main") - not a real ref to compare against later. Without
        // resolving it to the branch's actual current commit SHA here,
        // VersionComparer.IsNewer("<real sha>", "main") is always true (neither
        // side parses as a version, so any difference reads as an update),
        // meaning the very next "Check now" would always claim an update even
        // when nothing changed. Meaningless for a DockerImage addon - it has
        // no branch concept at all, only tags (see VersionCheckService).
        if (input.SourceKind == SourceKind.GitRepo && refType == SourceRefType.Branch)
        {
            var sha = await gitClient.GetLatestCommitShaAsync(input.RepoUrl, trackedBranch ?? "main", ct);
            input = input with { DisplayRef = sha };
        }

        var outcome = await generator.GenerateAsync(input, ct);

        await fileWriter.PublishFromStagingAsync(input.Slug, ct);

        var addon = new ManagedAddon
        {
            Slug = input.Slug,
            Name = input.Name,
            SourceRepoUrl = input.RepoUrl,
            Description = input.Description,
            Ingress = input.Ingress,
            IngressPort = input.IngressPort,
            SourceKind = input.SourceKind,
            SourceRefType = refType,
            TrackedBranch = trackedBranch,
            HostNetwork = input.HostNetwork,
            ManualPorts = [.. input.ManualPorts ?? []],
            DockerfilePath = input.DockerfilePath,
            MapEntries = [.. input.MapEntries ?? []],
            ManualEnvVars = [.. input.ManualEnvVars ?? []],
            ConfigFileTemplate = input.ConfigFileTemplate,
            ConfigFileDestPath = input.ConfigFileDestPath,
            AllowDistrolessDebugShell = input.AllowDistrolessDebugShell,
            PersistentPaths = PersistenceDetector.NormalizePaths(input.PersistentPaths ?? []),
            SharedPaths = PersistenceDetector.NormalizePaths(input.SharedPaths ?? [], allowUnderData: true),
            DetectedPersistentPaths = [.. outcome.Analysis.PersistentPathCandidates],
            PersistenceCheckedAt = DateTimeOffset.UtcNow,
            PersistenceReviewed = true,
            CurrentRef = input.DisplayRef,
            CurrentVersion = input.Version,
            UpdatePolicy = policy,
            LastWrittenAt = DateTimeOffset.UtcNow,
            LastCheckedAt = DateTimeOffset.UtcNow,
        };

        await repository.AddAsync(addon, ct);
        return addon;
    }

    /// <summary>
    /// Checks one addon against its source repo. On an update, regenerates and
    /// stages the new files; publishes them immediately too when the addon's
    /// policy is Auto. Never touches Home Assistant itself — publishing here
    /// only means "copied into the local addon store folder", the same as
    /// step one. Swallows generation/clone failures into LastError instead of
    /// throwing, so a scheduler loop over many addons doesn't abort on one bad repo.
    /// </summary>
    public async Task<ManagedAddon> CheckAndRegenerateAsync(string slug, CancellationToken ct)
    {
        var addon = await repository.GetBySlugAsync(slug, ct)
            ?? throw new InvalidOperationException($"No managed addon '{slug}'.");

        addon.LastCheckedAt = DateTimeOffset.UtcNow;

        try
        {
            var check = await versionChecker.CheckAsync(addon, ct);
            if (!check.HasUpdate || check.LatestRef is null)
            {
                addon.LastError = null;
                await BackfillPersistenceDetectionAsync(addon, ct);
            }
            else
            {
                var version = DeriveVersion(addon, check.LatestRef);

                // `git clone --branch` only accepts a real branch/tag name - a
                // resolved commit SHA (what branch tracking hands us here)
                // isn't valid there, so clone the branch itself and just
                // record the SHA as what we generated from.
                var cloneRef = addon.SourceRefType == SourceRefType.Branch
                    ? addon.TrackedBranch ?? "main"
                    : check.LatestRef;

                var outcome = await generator.GenerateAsync(
                    new NewAddonInput(
                        addon.SourceRepoUrl, cloneRef, check.LatestRef, addon.Slug, addon.Name, addon.Description,
                        version, addon.Ingress, addon.IngressPort, addon.DockerfilePath, addon.MapEntries,
                        addon.ManualEnvVars, addon.ConfigFileTemplate, addon.ConfigFileDestPath,
                        addon.AllowDistrolessDebugShell, addon.PersistentPaths, addon.SharedPaths, addon.SourceKind,
                        addon.HostNetwork, addon.ManualPorts),
                    ct);

                addon.DetectedPersistentPaths = [.. outcome.Analysis.PersistentPathCandidates];
                addon.PersistenceCheckedAt = DateTimeOffset.UtcNow;

                addon.StagedRef = check.LatestRef;
                addon.StagedVersion = version;
                addon.LastError = null;

                if (addon.UpdatePolicy == UpdatePolicy.Auto)
                {
                    await PublishStagedInternalAsync(addon, ct);
                }
            }
        }
        catch (Exception ex)
        {
            addon.LastError = ex.Message;
        }

        await repository.UpdateAsync(addon, ct);
        return addon;
    }

    /// <summary>
    /// An addon created before persistence detection existed has never had
    /// its repo scanned for state paths - and it's exactly those older addons
    /// that may already be silently losing data on updates. Scans once, on the
    /// next check, without touching any generated files. Best effort: a
    /// failure here (network, clone) is not worth flagging the addon as errored.
    /// </summary>
    private async Task BackfillPersistenceDetectionAsync(ManagedAddon addon, CancellationToken ct)
    {
        // A DockerImage addon always has PersistenceCheckedAt set from
        // CreateAsync (persistence candidates come straight from the image's
        // own inspected Volumes, no separate re-scan needed) - this guard is
        // for a git-sourced addon only, but kept explicit rather than relying
        // on that always being true, since generator.AnalyzeAsync below is
        // git-only and would try to `git clone` an image reference otherwise.
        if (addon.SourceKind == SourceKind.DockerImage ||
            addon.PersistenceCheckedAt is not null || string.IsNullOrEmpty(addon.CurrentRef))
        {
            return;
        }

        try
        {
            var cloneRef = addon.SourceRefType == SourceRefType.Branch
                ? addon.TrackedBranch ?? "main"
                : addon.CurrentRef;
            var analysis = await generator.AnalyzeAsync(addon.SourceRepoUrl, cloneRef, addon.DockerfilePath, ct);
            addon.DetectedPersistentPaths = [.. analysis.PersistentPathCandidates];
            addon.PersistenceCheckedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Copies whatever is currently staged for this addon into the published folder (manual-policy approval).</summary>
    public async Task<ManagedAddon> PublishStagedAsync(string slug, CancellationToken ct)
    {
        var addon = await repository.GetBySlugAsync(slug, ct)
            ?? throw new InvalidOperationException($"No managed addon '{slug}'.");

        if (string.IsNullOrEmpty(addon.StagedRef))
        {
            throw new InvalidOperationException($"'{slug}' has nothing staged.");
        }

        await PublishStagedInternalAsync(addon, ct);
        await repository.UpdateAsync(addon, ct);
        return addon;
    }

    /// <summary>
    /// Regenerates and republishes from the addon's current ref, with no
    /// version change and no upstream comparison — for when only
    /// addon-forge's own manual fields (map entries, extra env vars, icon,
    /// config file) changed, not the source repo itself. Publishes directly since, like
    /// <see cref="CreateAsync"/>, this is an explicit, one-off user action.
    /// </summary>
    public async Task<ManagedAddon> RegenerateAsync(string slug, CancellationToken ct)
    {
        var addon = await repository.GetBySlugAsync(slug, ct)
            ?? throw new InvalidOperationException($"No managed addon '{slug}'.");

        if (string.IsNullOrEmpty(addon.CurrentRef))
        {
            throw new InvalidOperationException($"'{slug}' has never been generated yet.");
        }

        var cloneRef = addon.SourceRefType == SourceRefType.Branch
            ? addon.TrackedBranch ?? "main"
            : addon.CurrentRef;

        var outcome = await generator.GenerateAsync(
            new NewAddonInput(
                addon.SourceRepoUrl, cloneRef, addon.CurrentRef, addon.Slug, addon.Name, addon.Description,
                addon.CurrentVersion ?? "0.1.0", addon.Ingress, addon.IngressPort, addon.DockerfilePath,
                addon.MapEntries, addon.ManualEnvVars, addon.ConfigFileTemplate, addon.ConfigFileDestPath,
                addon.AllowDistrolessDebugShell, addon.PersistentPaths, addon.SharedPaths, addon.SourceKind,
                addon.HostNetwork, addon.ManualPorts),
            ct);

        addon.DetectedPersistentPaths = [.. outcome.Analysis.PersistentPathCandidates];
        addon.PersistenceCheckedAt = DateTimeOffset.UtcNow;
        await fileWriter.PublishFromStagingAsync(addon.Slug, ct);
        addon.LastWrittenAt = DateTimeOffset.UtcNow;
        addon.LastError = null;
        await repository.UpdateAsync(addon, ct);
        return addon;
    }

    /// <summary>
    /// Permanently deletes the addon's files from the local addon store (and
    /// any staged files) and stops tracking it. Unlike just forgetting it,
    /// this actually removes /addons/&lt;slug&gt; from disk — irreversible, and if
    /// the addon is currently installed in Home Assistant, leaves it without
    /// the files it was built from. Callers are responsible for confirming
    /// with the user first.
    /// </summary>
    public async Task DeleteAsync(string slug, CancellationToken ct)
    {
        await fileWriter.DeleteAsync(slug, ct);
        await repository.DeleteAsync(slug, ct);
    }

    private async Task PublishStagedInternalAsync(ManagedAddon addon, CancellationToken ct)
    {
        await fileWriter.PublishFromStagingAsync(addon.Slug, ct);
        addon.CurrentRef = addon.StagedRef;
        addon.CurrentVersion = addon.StagedVersion;
        addon.LastWrittenAt = DateTimeOffset.UtcNow;
        addon.StagedRef = null;
        addon.StagedVersion = null;
    }

    private static string DeriveVersion(ManagedAddon addon, string latestRef) => addon.SourceRefType switch
    {
        SourceRefType.Tag => latestRef.TrimStart('v', 'V'),
        _ => VersionComparer.IncrementPatch(addon.CurrentVersion ?? "0.1.0"),
    };
}
