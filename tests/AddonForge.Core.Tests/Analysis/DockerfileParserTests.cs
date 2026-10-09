using AddonForge.Core.Analysis;
using AddonForge.Core.Models;
using Xunit;

namespace AddonForge.Core.Tests.Analysis;

public class DockerfileParserTests
{
    private const string SampleDockerfile = """
        FROM node:20-alpine
        EXPOSE 3000
        EXPOSE 3001/udp
        VOLUME ["/data", "/config"]
        ENV NODE_ENV=production PORT=3000
        ENV API_TOKEN=""
        CMD ["node", "server.js"]
        """;

    [Fact]
    public void ParsePorts_ReadsExposeWithAndWithoutProtocol()
    {
        var ports = DockerfileParser.ParsePorts(SampleDockerfile);

        Assert.Contains(ports, p => p.ContainerPort == 3000 && p.Protocol == PortProtocol.Tcp);
        Assert.Contains(ports, p => p.ContainerPort == 3001 && p.Protocol == PortProtocol.Udp);
    }

    [Fact]
    public void ParseVolumes_ReadsJsonArrayForm()
    {
        var volumes = DockerfileParser.ParseVolumes(SampleDockerfile);

        Assert.Contains(volumes, v => v.ContainerPath == "/data");
        Assert.Contains(volumes, v => v.ContainerPath == "/config");
    }

    [Fact]
    public void ParseEnvVars_ReadsMultiPairLine()
    {
        var vars = DockerfileParser.ParseEnvVars(SampleDockerfile);

        var nodeEnv = Assert.Single(vars, v => v.Name == "NODE_ENV");
        Assert.Equal("production", nodeEnv.DefaultValue);

        var port = Assert.Single(vars, v => v.Name == "PORT");
        Assert.Equal(InferredOptionType.Int, port.InferredType);
    }

    [Fact]
    public void ParseEnvVars_EmptyDefaultWithSecretName_IsPasswordTypeNotAmbiguous()
    {
        var vars = DockerfileParser.ParseEnvVars(SampleDockerfile);

        var token = Assert.Single(vars, v => v.Name == "API_TOKEN");
        Assert.Equal(InferredOptionType.Password, token.InferredType);
        Assert.False(token.IsAmbiguous);
    }

    [Fact]
    public void ParseEnvVars_NoDefaultAndUnclearName_IsAmbiguous()
    {
        const string dockerfile = "FROM alpine\nENV MODE\n";

        // Legacy `ENV KEY value` form requires a value; a bare `ENV MODE` with
        // nothing else isn't matched at all by design (nothing to record).
        var vars = DockerfileParser.ParseEnvVars(dockerfile);

        Assert.Empty(vars);
    }

    [Fact]
    public void ParsePorts_NoDockerfileContent_ReturnsEmpty()
    {
        Assert.Empty(DockerfileParser.ParsePorts("FROM alpine\nCMD [\"sh\"]"));
    }

    [Fact]
    public void ParseEnvVars_SystemPlumbingVars_AreExcluded()
    {
        const string dockerfile = "FROM python:3.12-slim\nENV PATH=/home/app/.local/bin:$PATH PYTHONUNBUFFERED=1\nENV APP_PORT=8000\n";

        var vars = DockerfileParser.ParseEnvVars(dockerfile);

        Assert.DoesNotContain(vars, v => v.Name == "PATH");
        Assert.DoesNotContain(vars, v => v.Name == "PYTHONUNBUFFERED");
        Assert.Contains(vars, v => v.Name == "APP_PORT");
    }

    // Regression test, shaped after navidrome's real Dockerfile: `EXPOSE
    // ${ND_PORT}` where ND_PORT is both the app's configurable listening
    // port AND declared as a plain ENV default earlier in the same stage.
    // The old parser only handled a literal integer in EXPOSE, so this port
    // was silently dropped - the generated addon had no port mapping at all.
    [Fact]
    public void ParsePorts_ExposeReferencesPrecedingEnvVar_ResolvesIt()
    {
        const string dockerfile = "FROM alpine\nENV ND_PORT=4533\nEXPOSE ${ND_PORT}\nENTRYPOINT [\"/app/navidrome\"]\n";

        var ports = DockerfileParser.ParsePorts(dockerfile);

        Assert.Contains(ports, p => p.ContainerPort == 4533 && p.Protocol == PortProtocol.Tcp);
    }

    [Fact]
    public void ParsePorts_ExposeReferencesUndeclaredVar_IsSkippedNotCrashed()
    {
        const string dockerfile = "FROM alpine\nEXPOSE ${UNDECLARED_PORT}\n";

        var ports = DockerfileParser.ParsePorts(dockerfile);

        Assert.Empty(ports);
    }

    [Fact]
    public void ParsePorts_ExposeReferencesVarWithoutBraces_ResolvesIt()
    {
        const string dockerfile = "FROM alpine\nENV APP_PORT=9000\nEXPOSE $APP_PORT\n";

        var ports = DockerfileParser.ParsePorts(dockerfile);

        Assert.Contains(ports, p => p.ContainerPort == 9000);
    }

    [Theory]
    [InlineData("AGENT_BYPASS_DB_VOLUME_CHECK", "true", InferredOptionType.Bool)]
    [InlineData("BYPASS_CACHE", "false", InferredOptionType.Bool)]
    [InlineData("DB_PASS", "x", InferredOptionType.Password)]
    [InlineData("ADMIN_PASSWORD", "x", InferredOptionType.Password)]
    [InlineData("MYSQL_ROOT_PASSWD", "x", InferredOptionType.Password)]
    public void BuildCandidate_PassOnlyMeansSecretAsAWholeWord(string name, string value, InferredOptionType expected)
    {
        var candidate = Assert.Single(DockerfileParser.ParseEnvVars($"FROM alpine\nENV {name}={value}\n"));

        Assert.Equal(expected, candidate.InferredType);
    }

    // Regression (example-app): `ENV APP_SERIES=${APP_SERIES}`
    // became a literal "${APP_SERIES}" option default, exported over the image's
    // own 5.13 at runtime.
    [Fact]
    public void ParseEnvVars_ValueReferencesPrecedingArg_ResolvesIt()
    {
        const string dockerfile = "FROM ubuntu\nARG APP_SERIES=5.13\nENV APP_SERIES=${APP_SERIES} \\\n    DISPLAY=:99\n";

        var vars = DockerfileParser.ParseEnvVars(dockerfile);

        Assert.Equal("5.13", Assert.Single(vars, v => v.Name == "APP_SERIES").DefaultValue);
    }

    [Theory]
    [InlineData("${UNSET}", "")]
    [InlineData("${UNSET:-fallback}", "fallback")]
    [InlineData("${SET:-fallback}", "value")]
    [InlineData("${EMPTY:-fallback}", "fallback")]
    [InlineData("${EMPTY-fallback}", "")]
    [InlineData("${SET:+alt}", "alt")]
    [InlineData("${UNSET:+alt}", "")]
    [InlineData("$SET/sub", "value/sub")]
    public void ResolveInterpolation_FollowsComposeSemantics(string input, string expected)
    {
        var declared = new Dictionary<string, string> { ["SET"] = "value", ["EMPTY"] = "" };

        Assert.Equal(expected, DockerfileParser.ResolveInterpolation(input, declared));
    }
}
