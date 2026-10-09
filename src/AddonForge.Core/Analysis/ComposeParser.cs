using AddonForge.Core.Models;
using YamlDotNet.Serialization;

namespace AddonForge.Core.Analysis;

public record ComposeParseResult(
    string? ServiceName,
    List<PortMapping> Ports,
    List<VolumeMapping> Volumes,
    List<EnvVarCandidate> EnvVars)
{
    public static readonly ComposeParseResult Empty = new(null, [], [], []);
}

/// <summary>
/// Best-effort, deterministic reading of a docker-compose.yml: picks the
/// service that has a `build:` key (that's the one this addon will actually
/// build from a Dockerfile — a bundled `image:` dependency is out of scope,
/// see project v2 notes) and extracts ports/volumes/environment from it.
/// Handles both compose short syntax (strings) and the common long syntax
/// (mapping) forms; anything stranger is simply skipped rather than throwing,
/// since this is a best-effort signal, not a full compose-spec parser.
/// </summary>
public static class ComposeParser
{
    /// <param name="dockerfilePath">
    /// Repo-relative Dockerfile the addon is actually built from (null = the
    /// root "Dockerfile"). Volumes are only taken from the compose service
    /// that builds THAT Dockerfile - in a multi-service stack the "primary"
    /// service picked below for ports/env can be an unrelated one (e.g. a
    /// reverse proxy), and its volumes say nothing about where this image
    /// keeps its own state.
    /// </param>
    public static ComposeParseResult Parse(string composeContent, string? dockerfilePath = null)
    {
        object? root;
        try
        {
            root = new DeserializerBuilder().Build().Deserialize<object>(composeContent);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return ComposeParseResult.Empty;
        }

        if (root is not IDictionary<object, object> rootMap ||
            !rootMap.TryGetValue("services", out var servicesObj) ||
            servicesObj is not IDictionary<object, object> services ||
            services.Count == 0)
        {
            return ComposeParseResult.Empty;
        }

        var primary = services
            .Select(kv => (Name: kv.Key?.ToString(), Service: kv.Value as IDictionary<object, object>))
            .Where(s => s.Service is not null)
            .OrderByDescending(s => s.Service!.ContainsKey("build")) // prefer the buildable service
            .FirstOrDefault();

        if (primary.Service is null)
        {
            return ComposeParseResult.Empty;
        }

        var target = NormalizeRepoPath(string.IsNullOrWhiteSpace(dockerfilePath) ? "Dockerfile" : dockerfilePath);
        var builder = services
            .Select(kv => kv.Value as IDictionary<object, object>)
            .FirstOrDefault(s => s is not null && s.TryGetValue("build", out var b) && ResolveBuildDockerfile(b) == target);

        return new ComposeParseResult(
            primary.Name,
            ParsePorts(primary.Service),
            builder is null ? [] : ParseVolumes(builder),
            ParseEnvironment(primary.Service));
    }

    private static string? ResolveBuildDockerfile(object? build)
    {
        switch (build)
        {
            case string context:
                return NormalizeRepoPath($"{context}/Dockerfile");
            case IDictionary<object, object> map:
                var ctx = map.TryGetValue("context", out var c) ? c?.ToString() ?? "." : ".";
                var file = map.TryGetValue("dockerfile", out var d) ? d?.ToString() ?? "Dockerfile" : "Dockerfile";
                return NormalizeRepoPath($"{ctx}/{file}");
            default:
                return null;
        }
    }

    private static string NormalizeRepoPath(string path)
    {
        var stack = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == ".." && stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(part);
        }

        return string.Join('/', stack);
    }

    private static List<PortMapping> ParsePorts(IDictionary<object, object> service)
    {
        var result = new List<PortMapping>();
        if (!service.TryGetValue("ports", out var portsObj) || portsObj is not IEnumerable<object> entries)
        {
            return result;
        }

        foreach (var entry in entries)
        {
            switch (entry)
            {
                case string s:
                    result.Add(ParsePortShortSyntax(s));
                    break;
                case IDictionary<object, object> map:
                    var protocol = map.TryGetValue("protocol", out var p) &&
                                    string.Equals(p?.ToString(), "udp", StringComparison.OrdinalIgnoreCase)
                        ? PortProtocol.Udp
                        : PortProtocol.Tcp;
                    if (map.TryGetValue("target", out var target) && int.TryParse(target?.ToString(), out var containerPort))
                    {
                        int? hostPort = map.TryGetValue("published", out var pub) && int.TryParse(pub?.ToString(), out var hp)
                            ? hp
                            : null;
                        result.Add(new PortMapping { ContainerPort = containerPort, Protocol = protocol, SuggestedHostPort = hostPort });
                    }

                    break;
            }
        }

        return result;
    }

    private static PortMapping ParsePortShortSyntax(string entry)
    {
        var protocol = PortProtocol.Tcp;
        var value = entry;
        var slashIndex = value.IndexOf('/');
        if (slashIndex >= 0)
        {
            if (string.Equals(value[(slashIndex + 1)..], "udp", StringComparison.OrdinalIgnoreCase))
            {
                protocol = PortProtocol.Udp;
            }

            value = value[..slashIndex];
        }

        var parts = value.Split(':');
        var containerPart = parts[^1];
        int? hostPort = parts.Length > 1 && int.TryParse(parts[^2], out var hp) ? hp : null;

        return int.TryParse(containerPart, out var containerPort)
            ? new PortMapping { ContainerPort = containerPort, Protocol = protocol, SuggestedHostPort = hostPort }
            : new PortMapping { ContainerPort = 0, Protocol = protocol };
    }

    private static List<VolumeMapping> ParseVolumes(IDictionary<object, object> service)
    {
        var result = new List<VolumeMapping>();
        if (!service.TryGetValue("volumes", out var volumesObj) || volumesObj is not IEnumerable<object> entries)
        {
            return result;
        }

        foreach (var entry in entries)
        {
            switch (entry)
            {
                case string s:
                    // `${DATA_DIR:-./data}:/data` - resolve first, or its own `:-` splits the entry.
                    var parts = DockerfileParser.ResolveInterpolation(s).Split(':');
                    var containerPath = parts.Length >= 2 ? parts[1] : parts[0];
                    var readOnly = parts.Length >= 3 && parts[2].Contains('r') && !parts[2].Contains('w');
                    if (!string.IsNullOrWhiteSpace(containerPath))
                    {
                        result.Add(new VolumeMapping { ContainerPath = containerPath, ReadOnly = readOnly });
                    }

                    break;
                case IDictionary<object, object> map when map.TryGetValue("target", out var t):
                    var ro = map.TryGetValue("read_only", out var r) && bool.TryParse(r?.ToString(), out var rb) && rb;
                    result.Add(new VolumeMapping { ContainerPath = t!.ToString()!, ReadOnly = ro });
                    break;
            }
        }

        return result;
    }

    private static List<EnvVarCandidate> ParseEnvironment(IDictionary<object, object> service)
    {
        var result = new List<EnvVarCandidate>();
        if (!service.TryGetValue("environment", out var envObj))
        {
            return result;
        }

        switch (envObj)
        {
            case IDictionary<object, object> map:
                foreach (var kv in map)
                {
                    var key = kv.Key.ToString()!;
                    if (DockerfileParser.IsUserConfigurable(key))
                    {
                        result.Add(DockerfileParser.BuildCandidate(key, ResolveOrNull(kv.Value?.ToString()), "compose environment"));
                    }
                }

                break;
            case IEnumerable<object> list:
                foreach (var entry in list)
                {
                    var s = entry?.ToString() ?? string.Empty;
                    var eqIndex = s.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        var key = s[..eqIndex];
                        if (DockerfileParser.IsUserConfigurable(key))
                        {
                            result.Add(DockerfileParser.BuildCandidate(key, ResolveOrNull(s[(eqIndex + 1)..]), "compose environment"));
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(s) && DockerfileParser.IsUserConfigurable(s))
                    {
                        // `- KEY` with no value: passed through from the host shell, no default we can infer.
                        result.Add(DockerfileParser.BuildCandidate(s, null, "compose environment"));
                    }
                }

                break;
        }

        return result;
    }

    /// <summary>`VNC_PASSWORD: ${VNC_PASSWORD:-}` means "no default", not the literal text.</summary>
    private static string? ResolveOrNull(string? value) =>
        value is null ? null : DockerfileParser.ResolveInterpolation(value);
}
