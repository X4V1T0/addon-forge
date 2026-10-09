using AddonForge.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AddonForge.Infrastructure.Tests.Llm;

/// <summary>
/// Regression test for a real bug: config.yaml/options.json use snake_case
/// keys (api_key), but the default .NET configuration binder only matches
/// property names case-insensitively - it doesn't strip underscores. Without
/// [ConfigurationKeyName("api_key")] on ApiKey, this silently binds to the
/// property's default (empty string) instead of the configured value.
/// </summary>
public class OpenAiCompatibleOptionsBindingTests
{
    [Fact]
    public void Bind_SnakeCaseApiKey_PopulatesApiKeyProperty()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["llm:endpoint"] = "https://openrouter.ai/api/v1",
                ["llm:api_key"] = "sk-or-test-key",
                ["llm:model"] = "openai/gpt-4o-mini",
            })
            .Build();

        var options = configuration.GetSection("llm").Get<OpenAiCompatibleOptions>();

        Assert.NotNull(options);
        Assert.Equal("https://openrouter.ai/api/v1", options!.Endpoint);
        Assert.Equal("sk-or-test-key", options.ApiKey);
        Assert.Equal("openai/gpt-4o-mini", options.Model);
    }
}
