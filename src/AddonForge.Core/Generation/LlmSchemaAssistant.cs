using System.Text.Json;
using System.Text.Json.Serialization;
using AddonForge.Core.Abstractions;
using AddonForge.Core.Models;

namespace AddonForge.Core.Generation;

/// <summary>
/// Only used when RepoAnalyzer flagged env vars as ambiguous (no default, no
/// self-describing name). Asks the LLM to guess a type/default for those
/// specific vars, validates the reply against the known HA option types via
/// <see cref="SchemaBuilder.TryApplyProposal"/>, retries once on an invalid
/// reply, and gives up in favor of the deterministic (manual-review) fallback
/// if it still doesn't validate.
/// </summary>
public static class LlmSchemaAssistant
{
    private const string SystemPrompt =
        "You map Docker environment variables to Home Assistant addon option types. " +
        "Reply with ONLY a JSON array, no prose, no markdown fences. Each element: " +
        "{\"name\": string, \"type\": \"str\"|\"int\"|\"bool\"|\"password\"|\"url\", \"default\": string|null}. " +
        "Use \"password\" for secrets/tokens/keys, \"url\" for endpoints, \"int\" only if the value is numeric, " +
        "\"default\": null when you cannot infer a safe default.";

    public static async Task<IReadOnlyList<EnvVarCandidate>> RefineAsync(
        ILlmClient llmClient,
        IReadOnlyList<EnvVarCandidate> ambiguous,
        string? readmeExcerpt,
        CancellationToken ct)
    {
        if (ambiguous.Count == 0)
        {
            return [];
        }

        var userPrompt = BuildUserPrompt(ambiguous, readmeExcerpt);
        string? retryHint = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var prompt = retryHint is null ? userPrompt : $"{userPrompt}\n\n{retryHint}";
                var raw = await llmClient.CompleteAsync(new LlmRequest(SystemPrompt, prompt), ct);
                var proposals = ParseProposals(raw);

                if (proposals is not null)
                {
                    var refined = ApplyProposals(ambiguous, proposals);
                    if (refined.Count > 0)
                    {
                        return refined;
                    }
                }
            }
            catch
            {
                // network/parse failure - fall through to retry, then give up.
            }

            retryHint = "Your previous reply could not be parsed as the required JSON array. Reply with ONLY the JSON array this time.";
        }

        return [];
    }

    private static string BuildUserPrompt(IReadOnlyList<EnvVarCandidate> ambiguous, string? readmeExcerpt)
    {
        var names = string.Join(", ", ambiguous.Select(v => v.Name));
        var context = string.IsNullOrWhiteSpace(readmeExcerpt) ? string.Empty : $"\nProject description: {readmeExcerpt}";
        return $"Environment variables with no clear default or type: {names}.{context}";
    }

    private static List<ProposalDto>? ParseProposals(string raw)
    {
        var jsonStart = raw.IndexOf('[');
        var jsonEnd = raw.LastIndexOf(']');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<ProposalDto>>(
                raw[jsonStart..(jsonEnd + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<EnvVarCandidate> ApplyProposals(IReadOnlyList<EnvVarCandidate> ambiguous, List<ProposalDto> proposals)
    {
        var byName = ambiguous.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<EnvVarCandidate>();

        foreach (var proposal in proposals)
        {
            if (proposal.Name is null || proposal.Type is null || !byName.TryGetValue(proposal.Name, out var original))
            {
                continue;
            }

            if (!SchemaBuilder.TryApplyProposal(original, proposal.Type, proposal.Default, out _, out _) ||
                !Enum.TryParse<InferredOptionType>(proposal.Type, ignoreCase: true, out var type))
            {
                continue;
            }

            result.Add(new EnvVarCandidate
            {
                Name = original.Name,
                DefaultValue = proposal.Default,
                InferredType = type,
                IsAmbiguous = false,
                Source = $"{original.Source} (LLM-assisted)",
            });
        }

        return result;
    }

    private record ProposalDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("default")] string? Default);
}
