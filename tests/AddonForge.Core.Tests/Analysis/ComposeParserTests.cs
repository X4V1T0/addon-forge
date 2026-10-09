using AddonForge.Core.Analysis;
using AddonForge.Core.Models;
using Xunit;

namespace AddonForge.Core.Tests.Analysis;

public class ComposeParserTests
{
    private const string SampleCompose = """
        services:
          db:
            image: postgres:16
            environment:
              POSTGRES_PASSWORD: changeme
          app:
            build: .
            ports:
              - "8080:80"
              - "9000:9000/udp"
            volumes:
              - ./data:/data
              - config:/config:ro
            environment:
              - PUID=1000
              - PGID=1000
              - TZ=Etc/UTC
              - ADMIN_PASSWORD
        """;

    [Fact]
    public void Parse_PicksServiceWithBuildKey_NotFirstService()
    {
        var result = ComposeParser.Parse(SampleCompose);

        Assert.Equal("app", result.ServiceName);
    }

    [Fact]
    public void Parse_ExtractsPortsWithProtocol()
    {
        var result = ComposeParser.Parse(SampleCompose);

        Assert.Contains(result.Ports, p => p.ContainerPort == 80 && p.Protocol == PortProtocol.Tcp && p.SuggestedHostPort == 8080);
        Assert.Contains(result.Ports, p => p.ContainerPort == 9000 && p.Protocol == PortProtocol.Udp);
    }

    [Fact]
    public void Parse_ExtractsVolumesWithReadOnlyFlag()
    {
        var result = ComposeParser.Parse(SampleCompose);

        Assert.Contains(result.Volumes, v => v.ContainerPath == "/data" && !v.ReadOnly);
        Assert.Contains(result.Volumes, v => v.ContainerPath == "/config" && v.ReadOnly);
    }

    [Fact]
    public void Parse_ListSyntaxEnvironment_SplitsKeyValueAndBareKeys()
    {
        var result = ComposeParser.Parse(SampleCompose);

        var puid = Assert.Single(result.EnvVars, v => v.Name == "PUID");
        Assert.Equal("1000", puid.DefaultValue);
        Assert.False(puid.IsAmbiguous);

        var adminPassword = Assert.Single(result.EnvVars, v => v.Name == "ADMIN_PASSWORD");
        Assert.Null(adminPassword.DefaultValue);
        Assert.Equal(InferredOptionType.Password, adminPassword.InferredType);
    }

    [Fact]
    public void Parse_MapSyntaxEnvironment_IsSupported()
    {
        const string compose = """
            services:
              app:
                build: .
                environment:
                  API_TOKEN: secret123
                  PORT: "3000"
            """;

        var result = ComposeParser.Parse(compose);

        var token = Assert.Single(result.EnvVars, v => v.Name == "API_TOKEN");
        Assert.Equal(InferredOptionType.Password, token.InferredType);

        var port = Assert.Single(result.EnvVars, v => v.Name == "PORT");
        Assert.Equal(InferredOptionType.Int, port.InferredType);
    }

    [Fact]
    public void Parse_NoServicesKey_ReturnsEmptyResult()
    {
        var result = ComposeParser.Parse("not: a-compose-file");

        Assert.Null(result.ServiceName);
        Assert.Empty(result.Ports);
        Assert.Empty(result.Volumes);
        Assert.Empty(result.EnvVars);
    }

    [Fact]
    public void Parse_InvalidYaml_ReturnsEmptyResultInsteadOfThrowing()
    {
        var result = ComposeParser.Parse("services: [this is not valid: yaml: at all");

        Assert.Null(result.ServiceName);
        Assert.Empty(result.Ports);
    }
    private const string MultiService = """
        services:
          proxy:
            image: caddy:2
            volumes:
              - caddy-data:/data
              - caddy-config:/config
          controller:
            build:
              context: .
              dockerfile: docker/Dockerfile.aio
            volumes:
              - state:/var/sub-wave
        """;

    [Fact]
    public void Parse_TakesVolumesFromTheServiceThatBuildsTheChosenDockerfile()
    {
        var result = ComposeParser.Parse(MultiService, "docker/Dockerfile.aio");

        var v = Assert.Single(result.Volumes);
        Assert.Equal("/var/sub-wave", v.ContainerPath);
    }

    [Fact]
    public void Parse_NoServiceBuildsTheChosenDockerfile_ReturnsNoVolumes()
    {
        var result = ComposeParser.Parse(MultiService, "docker/Other.Dockerfile");

        Assert.Empty(result.Volumes);
    }

    // Regression (example-app): `${VAR:-}` defaults were kept
    // literally, and `${DIR:-./x}/config:/target` split on its own `:-`.
    [Fact]
    public void Parse_InterpolatedValues_ResolveToTheirDefaults()
    {
        const string compose = """
            services:
              app:
                build: .
                environment:
                  VNC_PASSWORD: ${VNC_PASSWORD:-}
                  RWC_PORT: ${RWC_PORT:-8765}
                volumes:
                  - ${APP_DATA_DIR:-./app-data}/config:/home/app/.config/app
            """;
        var result = ComposeParser.Parse(compose);

        Assert.Null(Assert.Single(result.EnvVars, v => v.Name == "VNC_PASSWORD").DefaultValue);
        Assert.Equal("8765", Assert.Single(result.EnvVars, v => v.Name == "RWC_PORT").DefaultValue);
        Assert.Equal("/home/app/.config/app", Assert.Single(result.Volumes).ContainerPath);
    }
}
