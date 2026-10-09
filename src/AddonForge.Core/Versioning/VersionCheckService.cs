using AddonForge.Core.Abstractions;
using AddonForge.Core.Models;

namespace AddonForge.Core.Versioning;

public record VersionCheckResult(bool HasUpdate, string? LatestRef);

/// <summary>
/// Asks the source (via IGitClient for a GitRepo addon, IContainerRegistryClient
/// for a DockerImage one - see ManagedAddon.SourceKind) what its latest ref
/// is and compares it against what addon-forge currently has on record. Does
/// not write anything anywhere — the scheduler decides what to do with the result.
/// </summary>
public class VersionCheckService(IGitClient gitClient, IContainerRegistryClient registryClient)
{
    public async Task<VersionCheckResult> CheckAsync(ManagedAddon addon, CancellationToken ct)
    {
        var latestRef = addon.SourceKind == SourceKind.DockerImage
            ? await registryClient.GetLatestTagAsync(ImageReference.Parse(addon.SourceRepoUrl), ct)
            : addon.SourceRefType switch
            {
                SourceRefType.Tag => await gitClient.GetLatestTagAsync(addon.SourceRepoUrl, ct),
                SourceRefType.Branch => await gitClient.GetLatestCommitShaAsync(
                    addon.SourceRepoUrl, addon.TrackedBranch ?? "main", ct),
                _ => null,
            };

        if (latestRef is null)
        {
            return new VersionCheckResult(false, null);
        }

        return new VersionCheckResult(VersionComparer.IsNewer(latestRef, addon.CurrentRef), latestRef);
    }
}
