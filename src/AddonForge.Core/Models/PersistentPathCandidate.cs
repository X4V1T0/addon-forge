namespace AddonForge.Core.Models;

/// <summary>
/// A container path the app very likely writes state to that Home Assistant
/// would NOT keep across a rebuild (only /data survives) - found by
/// PersistenceDetector. <see cref="Recommended"/> marks the strong signals
/// worth pre-selecting; the rest are shown but left for the user to opt in.
/// </summary>
public sealed record PersistentPathCandidate(string Path, string Reason, bool Recommended);
