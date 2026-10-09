using AddonForge.Core.Models;

namespace AddonForge.Core.Analysis;

/// <summary>
/// Parses manually-entered ports (see ManagedAddon.ManualPorts) - for images
/// that don't declare EXPOSE/ExposedPorts at all (common for one built around
/// `network_mode: host`, e.g. some reverse proxies) and so addon-forge has nothing to
/// detect on its own. One per line:
/// `container_port[/protocol][:host_port][:description]` - e.g. "443/udp",
/// "81/tcp:81:Admin UI", or just "8080" (defaults: tcp, host port = container
/// port). A description can itself contain colons (everything after the
/// second one is taken verbatim), since "HTTP/3 (QUIC)"-style text is common.
/// </summary>
public static class ManualPortParser
{
    public readonly record struct ParsedPort(PortMapping Port, string? Description);

    public static ParsedPort? Parse(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        var parts = line.Split(':', 3, StringSplitOptions.TrimEntries);
        var portAndProtocol = parts[0];

        var slashIndex = portAndProtocol.IndexOf('/');
        var portPart = slashIndex >= 0 ? portAndProtocol[..slashIndex] : portAndProtocol;
        var protocol = slashIndex >= 0 &&
                       portAndProtocol[(slashIndex + 1)..].Equals("udp", StringComparison.OrdinalIgnoreCase)
            ? PortProtocol.Udp
            : PortProtocol.Tcp;

        if (!int.TryParse(portPart, out var containerPort) || containerPort is < 1 or > 65535)
        {
            return null;
        }

        int? hostPort = parts.Length > 1 && int.TryParse(parts[1], out var hp) ? hp : null;
        var description = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : null;

        return new ParsedPort(
            new PortMapping { ContainerPort = containerPort, Protocol = protocol, SuggestedHostPort = hostPort },
            description);
    }

    /// <summary>Parses every line, silently skipping ones that don't parse (same "harmless to miss, never crashes" spirit as the rest of addon-forge's own text-entry parsers).</summary>
    public static (List<PortMapping> Ports, Dictionary<string, string> Descriptions) ParseAll(IEnumerable<string> rawLines)
    {
        var ports = new List<PortMapping>();
        var descriptions = new Dictionary<string, string>();

        foreach (var line in rawLines)
        {
            if (Parse(line) is not { } parsed)
            {
                continue;
            }

            ports.Add(parsed.Port);
            if (parsed.Description is not null)
            {
                var key = $"{parsed.Port.ContainerPort}/{(parsed.Port.Protocol == PortProtocol.Udp ? "udp" : "tcp")}";
                descriptions[key] = parsed.Description;
            }
        }

        return (ports, descriptions);
    }
}
