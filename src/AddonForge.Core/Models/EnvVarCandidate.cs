namespace AddonForge.Core.Models;

/// <summary>
/// Inferred type for an env var, mapped to a Home Assistant addon options
/// schema type. See https://developers.home-assistant.io/docs/add-ons/configuration#option-types
/// </summary>
public enum InferredOptionType
{
    Str,
    Int,
    Bool,
    Password,
    Url,
}

/// <summary>
/// An environment variable discovered in the source repo's Dockerfile and/or
/// docker-compose.yml, candidate for becoming a Home Assistant addon option.
/// </summary>
public class EnvVarCandidate
{
    public required string Name { get; init; }

    public string? DefaultValue { get; init; }

    public InferredOptionType InferredType { get; init; } = InferredOptionType.Str;

    /// <summary>
    /// True when the name/default gives no reliable signal about type or
    /// purpose (e.g. "MODE" with no default) — a candidate for LLM-assisted
    /// mapping instead of the deterministic heuristic.
    /// </summary>
    public bool IsAmbiguous { get; init; }

    /// <summary>Where this candidate came from, for diagnostics shown in the UI.</summary>
    public required string Source { get; init; }
}
