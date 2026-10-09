using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AddonForge.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace AddonForge.Infrastructure.Llm;

/// <summary>
/// Talks to any OpenAI-compatible /chat/completions endpoint — OpenAI itself,
/// or a local Ollama/LM Studio instance pointed at from the addon's options.
/// Used only for the two assist steps (schema proposal, docs summary); every
/// caller is responsible for validating the result before it touches a file.
/// </summary>
public class OpenAiCompatibleClient(HttpClient httpClient, IOptionsMonitor<OpenAiCompatibleOptions> options) : ILlmClient
{
    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var opts = options.CurrentValue;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{opts.Endpoint.TrimEnd('/')}/chat/completions");
        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        }

        httpRequest.Content = JsonContent.Create(new ChatCompletionRequest(
            opts.Model,
            [
                new ChatMessage("system", request.SystemPrompt),
                new ChatMessage("user", request.UserPrompt),
            ]));

        using var response = await httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(ct)
            ?? throw new InvalidOperationException("Empty response from LLM endpoint.");

        return payload.Choices.FirstOrDefault()?.Message.Content
            ?? throw new InvalidOperationException("LLM response had no choices.");
    }

    private record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages);

    private record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] ChatChoice[] Choices);

    private record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);
}
