using AddonForge.Core.Generation;
using Xunit;

namespace AddonForge.Core.Tests.Generation;

public class HomeAssistantArchTests
{
    [Theory]
    [InlineData("amd64", null, "amd64")]
    [InlineData("386", null, "i386")]
    [InlineData("arm64", null, "aarch64")]
    [InlineData("arm64", "v8", "aarch64")]
    [InlineData("arm", "v7", "armv7")]
    [InlineData("arm", "v6", "armhf")]
    public void Map_KnownDockerPlatform_ReturnsHomeAssistantName(string architecture, string? variant, string expected)
    {
        Assert.Equal(expected, HomeAssistantArch.Map(architecture, variant));
    }

    // Regression coverage for a real bug found against a live Supervisor:
    // Docker's own "arm64" was passed straight through into config.yaml's
    // `arch:` list, and the Supervisor's schema - which only accepts aarch64,
    // amd64, armhf, armv7, i386 - refused to even load the generated addon.
    [Fact]
    public void Map_DockerArm64_IsNeverPassedThroughRaw()
    {
        Assert.NotEqual("arm64", HomeAssistantArch.Map("arm64", null));
    }

    [Theory]
    [InlineData("ppc64le")]
    [InlineData("s390x")]
    [InlineData("riscv64")]
    [InlineData("unknown")]
    [InlineData("mips64")]
    public void Map_NoHomeAssistantEquivalent_ReturnsNull(string architecture)
    {
        Assert.Null(HomeAssistantArch.Map(architecture, null));
    }

    [Theory]
    [InlineData("v5")]
    [InlineData(null)]
    [InlineData("v9")]
    public void Map_UnrecognizedArmVariant_ReturnsNullRatherThanGuess(string? variant)
    {
        Assert.Null(HomeAssistantArch.Map("arm", variant));
    }

    [Fact]
    public void Map_IsCaseInsensitive()
    {
        Assert.Equal("armv7", HomeAssistantArch.Map("ARM", "V7"));
    }
}
