namespace AddonForge.Core.Versioning;

/// <summary>
/// Compares two Git refs to decide if one is "newer". Semver-ish tags
/// (v1.2.3, 1.2.3-beta) are compared numerically; anything that doesn't parse
/// as a version (commit SHAs when tracking a branch, "latest", odd tag
/// schemes) falls back to "different means newer", since there's no ordering
/// to exploit there anyway — a branch tracker just wants to know HEAD moved.
/// </summary>
public static class VersionComparer
{
    public static bool IsNewer(string? candidateRef, string? currentRef)
    {
        if (string.IsNullOrEmpty(candidateRef))
        {
            return false;
        }

        if (string.IsNullOrEmpty(currentRef))
        {
            return true;
        }

        if (string.Equals(candidateRef, currentRef, StringComparison.Ordinal))
        {
            return false;
        }

        if (TryParseVersion(candidateRef, out var candidate) && TryParseVersion(currentRef, out var current))
        {
            return candidate > current;
        }

        return true;
    }

    public static bool TryParseVersion(string raw, out Version version)
    {
        var normalized = raw.Trim().TrimStart('v', 'V');

        var cutIndex = normalized.IndexOfAny(['-', '+']);
        if (cutIndex >= 0)
        {
            normalized = normalized[..cutIndex];
        }

        // System.Version requires at least a major.minor pair.
        if (!normalized.Contains('.'))
        {
            normalized += ".0";
        }

        return Version.TryParse(normalized, out version!);
    }

    /// <summary>
    /// Bumps the patch component of a semver-ish version string by 1. Used
    /// for branch-tracked addons, which have no upstream version number to
    /// key off — the exact commit they were generated from is already
    /// recorded separately (ManagedAddon.CurrentRef/StagedRef), so the
    /// version field itself just needs to keep going up: 0.1.0, 0.1.1, 0.1.2...
    /// Anything that doesn't parse as major.minor.patch resets to "0.1.0"
    /// rather than failing - this only ever needs to produce *a* valid,
    /// increasing version, not preserve an unparseable one.
    /// </summary>
    public static string IncrementPatch(string version)
    {
        if (TryParseVersion(version, out var parsed))
        {
            var patch = Math.Max(parsed.Build, 0) + 1;
            return $"{Math.Max(parsed.Major, 0)}.{Math.Max(parsed.Minor, 0)}.{patch}";
        }

        return "0.1.0";
    }
}
