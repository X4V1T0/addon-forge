using Microsoft.Extensions.Configuration;

namespace AddonForge.Infrastructure.Llm;

/// <summary>Bound from this addon's own config.yaml `options.llm` group.</summary>
public class OpenAiCompatibleOptions
{
    /// <summary>Base URL of an OpenAI-compatible API (OpenAI itself, or a local Ollama/LM Studio endpoint).</summary>
    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    // .NET's configuration binder matches property names case-insensitively,
    // but doesn't strip underscores - "api_key" in options.json/config.yaml
    // would silently never bind to ApiKey without this.
    [ConfigurationKeyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4o-mini";
}
