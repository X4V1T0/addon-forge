namespace AddonForge.Core.Generation;

/// <summary>
/// Translates a Docker/OCI platform (architecture + optional variant, e.g.
/// "arm64"/null or "arm"/"v7") to Home Assistant's own addon `arch:` naming.
/// Confirmed against the Supervisor's own schema, which accepts exactly
/// these five values and rejects anything else outright (a real addon-forge
/// bug: it passed Docker's own "arm64" straight through once, and the
/// Supervisor refused to even load the generated config.yaml). Registries
/// use Docker/OCI names; Home Assistant uses its own - the two only agree on
/// "amd64".
/// </summary>
public static class HomeAssistantArch
{
    /// <summary>Null for any platform with no Home Assistant equivalent (ppc64le, s390x, riscv64, an unrecognized ARM variant...) - safer to leave an architecture off the list than offer an install that can't actually run there.</summary>
    public static string? Map(string architecture, string? variant) => architecture.ToLowerInvariant() switch
    {
        "amd64" => "amd64",
        "386" => "i386",
        "arm64" => "aarch64",
        "arm" when string.Equals(variant, "v7", StringComparison.OrdinalIgnoreCase) => "armv7",
        "arm" when string.Equals(variant, "v6", StringComparison.OrdinalIgnoreCase) => "armhf",
        _ => null,
    };
}
