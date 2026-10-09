namespace AddonForge.Core.Models;

/// <summary>
/// Derived status of a managed addon, shown in the dashboard.
/// </summary>
public enum AddonStatus
{
    UpToDate = 0,
    UpdatePending = 1,
    GenerationError = 2,
}
