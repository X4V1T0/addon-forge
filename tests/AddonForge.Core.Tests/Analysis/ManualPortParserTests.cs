using AddonForge.Core.Analysis;
using AddonForge.Core.Models;
using Xunit;

namespace AddonForge.Core.Tests.Analysis;

public class ManualPortParserTests
{
    [Fact]
    public void Parse_BarePortNumber_DefaultsToTcpAndNoHostPort()
    {
        var parsed = ManualPortParser.Parse("8080")!.Value;

        Assert.Equal(8080, parsed.Port.ContainerPort);
        Assert.Equal(PortProtocol.Tcp, parsed.Port.Protocol);
        Assert.Null(parsed.Port.SuggestedHostPort);
        Assert.Null(parsed.Description);
    }

    [Fact]
    public void Parse_UdpProtocol_IsRecognized()
    {
        var parsed = ManualPortParser.Parse("443/udp")!.Value;

        Assert.Equal(443, parsed.Port.ContainerPort);
        Assert.Equal(PortProtocol.Udp, parsed.Port.Protocol);
    }

    [Fact]
    public void Parse_WithHostPort_PopulatesSuggestedHostPort()
    {
        var parsed = ManualPortParser.Parse("81/tcp:81")!.Value;

        Assert.Equal(81, parsed.Port.ContainerPort);
        Assert.Equal(81, parsed.Port.SuggestedHostPort);
    }

    [Fact]
    public void Parse_WithDescription_PopulatesIt()
    {
        var parsed = ManualPortParser.Parse("81/tcp:81:Admin UI")!.Value;

        Assert.Equal("Admin UI", parsed.Description);
    }

    // A description can legitimately contain its own colons (e.g. "HTTP/3
    // (QUIC)" doesn't, but something like "Port: admin" would) - only the
    // first two colons are separators, the description is taken verbatim.
    [Fact]
    public void Parse_DescriptionContainingColon_IsNotFurtherSplit()
    {
        var parsed = ManualPortParser.Parse("443/udp:443:HTTP/3 (QUIC): fast path")!.Value;

        Assert.Equal("HTTP/3 (QUIC): fast path", parsed.Description);
    }

    [Fact]
    public void Parse_InvalidPortNumber_ReturnsNull()
    {
        Assert.Null(ManualPortParser.Parse("not-a-port"));
        Assert.Null(ManualPortParser.Parse("70000"));
        Assert.Null(ManualPortParser.Parse("0"));
    }

    [Fact]
    public void Parse_EmptyLine_ReturnsNull()
    {
        Assert.Null(ManualPortParser.Parse("   "));
    }

    [Fact]
    public void ParseAll_SkipsInvalidLinesAndCollectsDescriptions()
    {
        var (ports, descriptions) = ManualPortParser.ParseAll(
            ["80/tcp:80:HTTP", "443/tcp:443:HTTPS", "443/udp:443:HTTP/3 (QUIC)", "81/tcp:81", "garbage", ""]);

        Assert.Equal(4, ports.Count);
        Assert.Equal("HTTP", descriptions["80/tcp"]);
        Assert.Equal("HTTPS", descriptions["443/tcp"]);
        Assert.Equal("HTTP/3 (QUIC)", descriptions["443/udp"]);
        Assert.False(descriptions.ContainsKey("81/tcp"));
    }
}
