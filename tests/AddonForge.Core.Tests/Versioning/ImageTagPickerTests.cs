using AddonForge.Core.Versioning;
using Xunit;

namespace AddonForge.Core.Tests.Versioning;

public class ImageTagPickerTests
{
    [Fact]
    public void PickLatest_MixOfRealAndMovingTags_PicksHighestVersion()
    {
        var best = ImageTagPicker.PickLatest(["latest", "v1.5.0", "v1.6.3", "edge", "dev", "v1.6.2"]);

        Assert.Equal("v1.6.3", best);
    }

    [Fact]
    public void PickLatest_NoVersionLikeTags_ReturnsNull()
    {
        Assert.Null(ImageTagPicker.PickLatest(["latest", "edge", "nightly", "dev"]));
    }

    [Fact]
    public void PickLatest_EmptyList_ReturnsNull()
    {
        Assert.Null(ImageTagPicker.PickLatest([]));
    }

    // The same version can be published more than once under a build-variant
    // suffix (e.g. some images' "-debian" tags) - the plain tag should win
    // over a decorated one at the same version.
    [Fact]
    public void PickLatest_SameVersionWithVariantSuffix_PrefersPlainTag()
    {
        var best = ImageTagPicker.PickLatest(["v1.6.3-debian", "v1.6.3"]);

        Assert.Equal("v1.6.3", best);

        // Order shouldn't matter.
        Assert.Equal("v1.6.3", ImageTagPicker.PickLatest(["v1.6.3", "v1.6.3-debian"]));
    }

    [Fact]
    public void PickLatest_HigherVariantTagStillWinsOverLowerPlainTag()
    {
        var best = ImageTagPicker.PickLatest(["v1.6.3", "v1.7.0-debian"]);

        Assert.Equal("v1.7.0-debian", best);
    }

    [Fact]
    public void PickLatest_UnprefixedVersions_ParsesWithoutLeadingV()
    {
        Assert.Equal("2.1.0", ImageTagPicker.PickLatest(["1.0.0", "2.1.0", "latest"]));
    }
}
