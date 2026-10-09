using AddonForge.Core.Models;
using Xunit;

namespace AddonForge.Core.Tests.Models;

public class ImageReferenceTests
{
    [Fact]
    public void Parse_FullyQualifiedDockerHubReference_SplitsRegistryRepositoryAndTag()
    {
        var reference = ImageReference.Parse("docker.io/example/agent:v1.6.3");

        Assert.Equal("docker.io", reference.Registry);
        Assert.Equal("example/agent", reference.Repository);
        Assert.Equal("v1.6.3", reference.Tag);
        Assert.Null(reference.Digest);
    }

    [Fact]
    public void Parse_GhcrReferenceWithoutTag_LeavesTagNull()
    {
        var reference = ImageReference.Parse("ghcr.io/example/proxy");

        Assert.Equal("ghcr.io", reference.Registry);
        Assert.Equal("example/proxy", reference.Repository);
        Assert.Null(reference.Tag);
    }

    [Fact]
    public void Parse_BareDockerHubNameWithTag_AddsImplicitLibraryNamespace()
    {
        var reference = ImageReference.Parse("redis:7");

        Assert.Equal("docker.io", reference.Registry);
        Assert.Equal("library/redis", reference.Repository);
        Assert.Equal("7", reference.Tag);
    }

    [Fact]
    public void Parse_DockerHubNamespacedRepo_DoesNotAddLibraryPrefix()
    {
        var reference = ImageReference.Parse("example/agent");

        Assert.Equal("docker.io", reference.Registry);
        Assert.Equal("example/agent", reference.Repository);
    }

    [Fact]
    public void Parse_PrivateRegistryWithPort_TreatsFirstSegmentAsRegistryNotNamespace()
    {
        var reference = ImageReference.Parse("localhost:5000/myimage:1.0");

        Assert.Equal("localhost:5000", reference.Registry);
        Assert.Equal("myimage", reference.Repository);
        Assert.Equal("1.0", reference.Tag);
    }

    [Fact]
    public void Parse_ExplicitDockerHubLibraryNamespace_IsNotDoublePrefixed()
    {
        var reference = ImageReference.Parse("docker.io/library/redis:7");

        Assert.Equal("library/redis", reference.Repository);
    }

    [Fact]
    public void Parse_DigestReference_PopulatesDigestNotTag()
    {
        var reference = ImageReference.Parse(
            "ghcr.io/example/proxy@sha256:1111111111111111111111111111111111111111111111111111111111111111");

        Assert.Null(reference.Tag);
        Assert.Equal(
            "sha256:1111111111111111111111111111111111111111111111111111111111111111", reference.Digest);
    }

    [Fact]
    public void ApiHost_DockerHubAlias_ResolvesToRegistryOneHost()
    {
        var reference = ImageReference.Parse("example/agent:v1.6.3");

        Assert.Equal("registry-1.docker.io", reference.ApiHost);
    }

    [Fact]
    public void ApiHost_NonDockerHubRegistry_IsUnchanged()
    {
        var reference = ImageReference.Parse("ghcr.io/example/proxy:latest");

        Assert.Equal("ghcr.io", reference.ApiHost);
    }

    [Fact]
    public void ToString_DockerHubReference_OmitsRegistryPrefix()
    {
        var reference = ImageReference.Parse("example/agent:v1.6.3");

        Assert.Equal("example/agent:v1.6.3", reference.ToString());
    }

    [Fact]
    public void ToString_NonDockerHubReference_KeepsRegistryPrefix()
    {
        var reference = ImageReference.Parse("ghcr.io/example/proxy:latest");

        Assert.Equal("ghcr.io/example/proxy:latest", reference.ToString());
    }

    [Fact]
    public void WithTag_ReplacesTagAndClearsDigest()
    {
        var reference = ImageReference.Parse(
            "ghcr.io/example/proxy@sha256:1111111111111111111111111111111111111111111111111111111111111111");

        var withTag = reference.WithTag("2.0.0");

        Assert.Equal("2.0.0", withTag.Tag);
        Assert.Null(withTag.Digest);
    }

    [Fact]
    public void TagOrDigest_PrefersDigestOverTag()
    {
        var reference = new ImageReference("docker.io", "example/agent", "v1.6.3", "sha256:abc");

        Assert.Equal("sha256:abc", reference.TagOrDigest);
    }

    [Fact]
    public void TagOrDigest_NoTagOrDigest_DefaultsToLatest()
    {
        var reference = ImageReference.Parse("ghcr.io/example/proxy");

        Assert.Equal("latest", reference.TagOrDigest);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        Assert.Throws<FormatException>(() => ImageReference.Parse("   "));
    }
}
