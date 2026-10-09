namespace AddonForge.Core.Models;

/// <summary>
/// Strongly typed subset of a Home Assistant addon config.yaml that
/// addon-forge knows how to generate. Serialized to YAML by
/// Generation.AddonConfigGenerator. Only amd64 is populated for v1, but the
/// field is a list so multi-arch can be added later without a shape change.
/// </summary>
public class AddonManifest
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string Slug { get; init; }

    public required string Description { get; init; }

    public string? Url { get; init; }

    /// <summary>
    /// Set only for a SourceKind.DockerImage addon that needed no build at
    /// all (see AddonConfigGenerator) - the Supervisor pulls this image
    /// directly instead of building a Dockerfile, and this addon's own
    /// `Version` above is the tag it pulls. Never set alongside a Dockerfile
    /// file - the two are mutually exclusive as far as the Supervisor is
    /// concerned (`need_build` is just `ATTR_IMAGE not in data`).
    /// </summary>
    public string? Image { get; init; }

    public List<string> Arch { get; init; } = ["amd64"];

    public string Startup { get; init; } = "application";

    public string Boot { get; init; } = "auto";

    public bool Ingress { get; init; }

    public int? IngressPort { get; init; }

    /// <summary>
    /// If true, this addon runs on the host's own network instead of a
    /// Docker-managed one - required by images built around
    /// `network_mode: host` (e.g. some reverse proxies), which typically don't declare
    /// EXPOSE/ExposedPorts at all and rely on binding host ports directly.
    /// Confirmed directly against the Supervisor's own source
    /// (supervisor/docker/app.py): when this is true, it drops the `Ports`
    /// map entirely when actually starting the container (`if
    /// self.app.host_network or not self.app.ports: return None`) - Docker
    /// itself does no port publishing under host networking, so `Ports`
    /// below becomes purely informational once this is set, not functional.
    /// </summary>
    public bool HostNetwork { get; init; }

    /// <summary>Key: "container_port/protocol" (e.g. "8080/tcp"). Value: suggested host port or null (let user pick).</summary>
    public Dictionary<string, int?> Ports { get; init; } = [];

    /// <summary>Key: "container_port/protocol", matching <see cref="Ports"/>. Value: a human label shown next to that port in the addon's own Network tab.</summary>
    public Dictionary<string, string> PortsDescription { get; init; } = [];

    public List<string> Map { get; init; } = [];

    /// <summary>Default values for each option, keyed by option name exactly as it will appear in config.yaml.</summary>
    public Dictionary<string, object?> Options { get; init; } = [];

    /// <summary>HA schema DSL type string for each option (e.g. "str", "int(1,65535)", "password", "url", "bool").</summary>
    public Dictionary<string, object> Schema { get; init; } = [];
}
