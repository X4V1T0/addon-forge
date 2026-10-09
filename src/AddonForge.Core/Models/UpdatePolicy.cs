namespace AddonForge.Core.Models;

/// <summary>
/// Governs whether regenerated addon files are written straight to the local
/// addon store folder, or held in staging until the user approves them from
/// the addon-forge UI. Never controls installing/starting/updating the addon
/// itself in Home Assistant — that is always a manual action by the user.
/// </summary>
public enum UpdatePolicy
{
    Manual = 0,
    Auto = 1,
}
