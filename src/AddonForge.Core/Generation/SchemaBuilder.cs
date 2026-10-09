using AddonForge.Core.Models;

namespace AddonForge.Core.Generation;

/// <summary>
/// Turns the env vars RepoAnalyzer found into a Home Assistant `options` +
/// `schema` pair. Every candidate gets an entry in both, so the generated
/// config.yaml is always schema-valid on its own — ambiguous vars just get a
/// permissive `str?`/empty default that the user (or an LLM proposal, applied
/// via <see cref="ApplyProposal"/>) can tighten up before confirming.
/// </summary>
public static class SchemaBuilder
{
    public static (Dictionary<string, object?> Options, Dictionary<string, object> Schema) Build(
        IEnumerable<EnvVarCandidate> candidates)
    {
        var options = new Dictionary<string, object?>();
        var schema = new Dictionary<string, object>();

        foreach (var candidate in candidates)
        {
            options[candidate.Name] = ToOptionValue(candidate);
            schema[candidate.Name] = ToSchemaType(candidate);
        }

        return (options, schema);
    }

    /// <summary>
    /// Applies an LLM-proposed refinement for one ambiguous candidate. Only
    /// accepts a known HA option type and a value convertible to it — anything
    /// else is rejected so the caller can fall back to manual editing instead
    /// of writing garbage into config.yaml.
    /// </summary>
    public static bool TryApplyProposal(
        EnvVarCandidate original,
        string proposedType,
        string? proposedDefault,
        out object? optionValue,
        out object schemaValue)
    {
        optionValue = null;
        schemaValue = "str?";

        if (!KnownTypes.Contains(proposedType))
        {
            return false;
        }

        var candidate = new EnvVarCandidate
        {
            Name = original.Name,
            DefaultValue = proposedDefault,
            InferredType = Enum.Parse<InferredOptionType>(proposedType, ignoreCase: true),
            IsAmbiguous = false,
            Source = original.Source,
        };

        optionValue = ToOptionValue(candidate);
        schemaValue = ToSchemaType(candidate);
        return true;
    }

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(InferredOptionType.Str),
        nameof(InferredOptionType.Int),
        nameof(InferredOptionType.Bool),
        nameof(InferredOptionType.Password),
        nameof(InferredOptionType.Url),
    };

    private static object? ToOptionValue(EnvVarCandidate candidate) => candidate.InferredType switch
    {
        InferredOptionType.Int => int.TryParse(candidate.DefaultValue, out var i) ? i : 0,
        InferredOptionType.Bool => bool.TryParse(candidate.DefaultValue, out var b) && b,
        _ => candidate.DefaultValue ?? string.Empty,
    };

    private static object ToSchemaType(EnvVarCandidate candidate)
    {
        var baseType = candidate.InferredType switch
        {
            InferredOptionType.Int => "int",
            InferredOptionType.Bool => "bool",
            InferredOptionType.Password => "password",
            InferredOptionType.Url => "url",
            _ => "str",
        };

        // No reliable default (or we flagged it ambiguous) -> mark optional so
        // the generated config.yaml still validates even if left untouched.
        var optional = candidate.DefaultValue is null || candidate.IsAmbiguous;
        return optional ? $"{baseType}?" : baseType;
    }
}
