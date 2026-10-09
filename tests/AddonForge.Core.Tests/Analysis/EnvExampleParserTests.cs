using AddonForge.Core.Analysis;
using AddonForge.Core.Models;
using Xunit;

namespace AddonForge.Core.Tests.Analysis;

public class EnvExampleParserTests
{
    // Shaped after a real repo (subwave) that declares its required vars only
    // in .env.example, loading them into the container via `env_file: ./.env`
    // rather than listing each one in compose - Dockerfile/compose parsing
    // alone missed them entirely, so the generated addon had no way to
    // collect ADMIN_USER/ADMIN_PASS and the container crashed on boot.
    private const string SampleEnvExample = """
        # Admin credentials gate the wizard + admin UI. Mandatory in prod.
        ADMIN_USER=admin
        ADMIN_PASS=                       # generate one: openssl rand -hex 16

        # Public origin (used for absolute URLs, OG tags, manifest, sitemap).
        SITE_URL=

        # ═══ OPTIONAL — sensible defaults, uncomment to override ═══
        # STATE_DIR=./state
        """;

    [Fact]
    public void Parse_ActiveKeyWithDefault_IsCaptured()
    {
        var vars = EnvExampleParser.Parse(SampleEnvExample);

        var adminUser = Assert.Single(vars, v => v.Name == "ADMIN_USER");
        Assert.Equal("admin", adminUser.DefaultValue);
        Assert.False(adminUser.IsAmbiguous);
    }

    [Fact]
    public void Parse_EmptyValueWithInlineComment_StripsCommentAndInfersPasswordType()
    {
        var vars = EnvExampleParser.Parse(SampleEnvExample);

        var adminPass = Assert.Single(vars, v => v.Name == "ADMIN_PASS");
        Assert.Null(adminPass.DefaultValue);
        Assert.Equal(InferredOptionType.Password, adminPass.InferredType);
        // "PASS" alone (not just "PASSWORD") must be recognized so this isn't
        // just flagged ambiguous and left for manual/LLM review every time.
        Assert.False(adminPass.IsAmbiguous);
    }

    [Fact]
    public void Parse_CommentedOutOptionalLines_AreSkipped()
    {
        var vars = EnvExampleParser.Parse(SampleEnvExample);

        Assert.DoesNotContain(vars, v => v.Name == "STATE_DIR");
    }

    [Fact]
    public void Parse_QuotedValue_StripsQuotes()
    {
        var vars = EnvExampleParser.Parse("GREETING=\"hello world\"\n");

        var greeting = Assert.Single(vars);
        Assert.Equal("hello world", greeting.DefaultValue);
    }

    [Fact]
    public void RepoAnalyzer_EnvExampleOnlyFillsGapsNotAlreadyCoveredByComposeOrDockerfile()
    {
        var files = new RepoSourceFiles
        {
            DockerfileContent = "FROM alpine\nENV SITE_URL=https://placeholder.invalid\n",
            EnvExampleContent = SampleEnvExample,
        };

        var result = RepoAnalyzer.Analyze(files);

        // Dockerfile's value wins for a name .env.example also mentions...
        var siteUrl = Assert.Single(result.EnvVars, v => v.Name == "SITE_URL");
        Assert.Equal("https://placeholder.invalid", siteUrl.DefaultValue);

        // ...but ADMIN_USER/ADMIN_PASS, absent from the Dockerfile, still show up.
        Assert.Contains(result.EnvVars, v => v.Name == "ADMIN_USER");
        Assert.Contains(result.EnvVars, v => v.Name == "ADMIN_PASS");
    }
}
