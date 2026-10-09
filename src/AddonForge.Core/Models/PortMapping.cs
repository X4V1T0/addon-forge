namespace AddonForge.Core.Models;

public enum PortProtocol
{
    Tcp,
    Udp,
}

/// <summary>A container port discovered via EXPOSE and/or compose `ports:`.</summary>
public class PortMapping
{
    public required int ContainerPort { get; init; }

    public PortProtocol Protocol { get; init; } = PortProtocol.Tcp;

    /// <summary>Host port suggested by the compose file, if any (compose `ports:` has a host side).</summary>
    public int? SuggestedHostPort { get; init; }
}

/// <summary>A container volume path discovered via VOLUME and/or compose `volumes:`.</summary>
public class VolumeMapping
{
    public required string ContainerPath { get; init; }

    public bool ReadOnly { get; init; }
}
