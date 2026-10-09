namespace AddonForge.Core.Versioning;

/// <summary>
/// Picks which tag of a published image counts as "the latest version" to
/// track, out of everything IContainerRegistryClient.ListTagsAsync returns -
/// a repository's tags are a much noisier mix than git's own tags: a moving
/// alias like "latest"/"edge"/"nightly" sits right alongside real, immutable
/// version tags, and the same version can appear more than once under
/// build-variant suffixes (e.g. "v1.6.3" and "v1.6.3-debian" published side
/// by side). Only a tag that parses as a semver-ish version at all is
/// considered - the "recommended" reading given for tracking an image
/// (Docker/OCI has no equivalent of git's own annotated-tag/release concept
/// to lean on instead).
/// </summary>
public static class ImageTagPicker
{
    public static string? PickLatest(IEnumerable<string> tags)
    {
        string? best = null;
        Version? bestVersion = null;

        foreach (var tag in tags)
        {
            if (!VersionComparer.TryParseVersion(tag, out var version))
            {
                continue;
            }

            // On a tie (the same version under different build-variant
            // suffixes), prefer the plain tag - it's the more likely default
            // a user expects addon-forge to have picked over a decorated one.
            var isBetter = bestVersion is null || version > bestVersion ||
                           (version == bestVersion && best is not null && !tag.Contains('-') && best.Contains('-'));

            if (isBetter)
            {
                best = tag;
                bestVersion = version;
            }
        }

        return best;
    }
}
