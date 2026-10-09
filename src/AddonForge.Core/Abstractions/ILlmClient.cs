namespace AddonForge.Core.Abstractions;

/// <summary>
/// A single request to an OpenAI-compatible chat completions endpoint. Used
/// only for the two non-deterministic assist steps (options schema proposal,
/// docs summary) — never on the path that writes files directly.
/// </summary>
public record LlmRequest(string SystemPrompt, string UserPrompt);

public interface ILlmClient
{
    /// <summary>
    /// Returns the raw text completion. Callers that need structured output
    /// (e.g. a schema proposal) are responsible for parsing and validating it,
    /// and for falling back to manual mode if it doesn't validate.
    /// </summary>
    Task<string> CompleteAsync(LlmRequest request, CancellationToken ct);
}
