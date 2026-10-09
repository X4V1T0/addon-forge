using AddonForge.Core.Versioning;
using Xunit;

namespace AddonForge.Core.Tests.Versioning;

public class VersionComparerTests
{
    [Theory]
    [InlineData("v1.2.4", "v1.2.3", true)]
    [InlineData("1.2.4", "1.2.3", true)]
    [InlineData("v2.0.0", "v1.9.9", true)]
    [InlineData("v1.2.3", "v1.2.3", false)]
    [InlineData("v1.2.2", "v1.2.3", false)]
    [InlineData("v1.10.0", "v1.9.0", true)]
    public void IsNewer_SemverTags_ComparesNumerically(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, VersionComparer.IsNewer(candidate, current));
    }

    [Fact]
    public void IsNewer_NoCurrentRef_AnyCandidateIsNewer()
    {
        Assert.True(VersionComparer.IsNewer("v1.0.0", null));
    }

    [Fact]
    public void IsNewer_NoCandidateRef_NeverNewer()
    {
        Assert.False(VersionComparer.IsNewer(null, "v1.0.0"));
    }

    [Fact]
    public void IsNewer_IdenticalShaTrackingBranch_NotNewer()
    {
        const string sha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
        Assert.False(VersionComparer.IsNewer(sha, sha));
    }

    [Fact]
    public void IsNewer_DifferentShaTrackingBranch_IsNewer()
    {
        Assert.True(VersionComparer.IsNewer("a1b2c3d", "0000000"));
    }

    [Fact]
    public void IsNewer_OneSideNotSemver_FallsBackToDifferenceCheck()
    {
        // "latest" doesn't parse as a version; any difference counts as an update
        // since there's no ordering to compare.
        Assert.True(VersionComparer.IsNewer("latest", "v1.0.0"));
    }

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2.3-beta", true)]
    [InlineData("1.2", true)]
    [InlineData("latest", false)]
    [InlineData("main", false)]
    public void TryParseVersion_ParsesSemverLikeStrings(string raw, bool expectedSuccess)
    {
        Assert.Equal(expectedSuccess, VersionComparer.TryParseVersion(raw, out _));
    }

    // Regression test: Home Assistant tags the built image as
    // local/<arch>-addon-<slug>:<version>. A Docker reference tag only allows
    // [A-Za-z0-9_.-] - a previous version of this embedded the branch's
    // resolved commit SHA into the version (with a semver "+" separator, or
    // later a "-" one), which the user reasonably didn't want either: the
    // exact commit is already tracked separately in ManagedAddon.CurrentRef/
    // StagedRef, so the version itself should just count up.
    [Theory]
    [InlineData("0.1.0", "0.1.1")]
    [InlineData("0.1.9", "0.1.10")]
    [InlineData("2.3.7", "2.3.8")]
    public void IncrementPatch_BumpsPatchComponent(string current, string expected)
    {
        Assert.Equal(expected, VersionComparer.IncrementPatch(current));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    public void IncrementPatch_UnparseableInput_ResetsToBaseline(string current)
    {
        Assert.Equal("0.1.0", VersionComparer.IncrementPatch(current));
    }
}
