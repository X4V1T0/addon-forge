using AddonForge.Core.Generation;
using AddonForge.Core.Models;
using Xunit;

namespace AddonForge.Core.Tests.Generation;

public class SchemaBuilderTests
{
    [Fact]
    public void Build_CandidateWithDefault_IsRequiredType()
    {
        var candidate = new EnvVarCandidate { Name = "TZ", DefaultValue = "Etc/UTC", InferredType = InferredOptionType.Str, Source = "test" };

        var (options, schema) = SchemaBuilder.Build([candidate]);

        Assert.Equal("Etc/UTC", options["TZ"]);
        Assert.Equal("str", schema["TZ"]);
    }

    [Fact]
    public void Build_AmbiguousCandidate_IsOptionalTypeEvenWithoutQuestionMarkInName()
    {
        var candidate = new EnvVarCandidate { Name = "MODE", DefaultValue = null, InferredType = InferredOptionType.Str, IsAmbiguous = true, Source = "test" };

        var (options, schema) = SchemaBuilder.Build([candidate]);

        Assert.Equal(string.Empty, options["MODE"]);
        Assert.Equal("str?", schema["MODE"]);
    }

    [Fact]
    public void Build_IntCandidate_ConvertsDefaultToInt()
    {
        var candidate = new EnvVarCandidate { Name = "PORT", DefaultValue = "3000", InferredType = InferredOptionType.Int, Source = "test" };

        var (options, schema) = SchemaBuilder.Build([candidate]);

        Assert.Equal(3000, options["PORT"]);
        Assert.Equal("int", schema["PORT"]);
    }

    [Fact]
    public void Build_BoolCandidate_ConvertsDefaultToBool()
    {
        var candidate = new EnvVarCandidate { Name = "ENABLE_X", DefaultValue = "true", InferredType = InferredOptionType.Bool, Source = "test" };

        var (options, schema) = SchemaBuilder.Build([candidate]);

        Assert.Equal(true, options["ENABLE_X"]);
        Assert.Equal("bool", schema["ENABLE_X"]);
    }

    [Fact]
    public void TryApplyProposal_KnownType_Succeeds()
    {
        var original = new EnvVarCandidate { Name = "LOG_LEVEL", DefaultValue = null, IsAmbiguous = true, Source = "test" };

        var applied = SchemaBuilder.TryApplyProposal(original, "str", "info", out var optionValue, out var schemaValue);

        Assert.True(applied);
        Assert.Equal("info", optionValue);
        Assert.Equal("str", schemaValue);
    }

    [Fact]
    public void TryApplyProposal_UnknownType_Fails()
    {
        var original = new EnvVarCandidate { Name = "LOG_LEVEL", DefaultValue = null, IsAmbiguous = true, Source = "test" };

        var applied = SchemaBuilder.TryApplyProposal(original, "enum", "info", out _, out _);

        Assert.False(applied);
    }
}
