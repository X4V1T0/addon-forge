namespace AddonForge.Core.Models;

/// <summary>
/// What kind of ref addon-forge tracks on the source repo to detect new versions.
/// </summary>
public enum SourceRefType
{
    /// <summary>Track the latest semver-ish Git tag/release.</summary>
    Tag = 0,

    /// <summary>Track the HEAD commit SHA of a specific branch.</summary>
    Branch = 1,
}
