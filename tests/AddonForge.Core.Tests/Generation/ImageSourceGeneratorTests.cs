using AddonForge.Core.Generation;
using AddonForge.Core.Models;
using Xunit;

namespace AddonForge.Core.Tests.Generation;

public class ImageSourceGeneratorTests
{
    [Fact]
    public void BuildRepoAnalysisResult_MapsPortsVolumesAndEnvFromInspection()
    {
        var inspection = new ImageInspectionResult
        {
            Digest = "sha256:abc",
            Ports = [new PortMapping { ContainerPort = 8080 }],
            Volumes = [new VolumeMapping { ContainerPath = "/data/nginx/logs" }],
            RawEnv = ["TZ=UTC", "ADMIN_PASSWORD="],
        };

        var analysis = ImageSourceGenerator.BuildRepoAnalysisResult(inspection);

        Assert.True(analysis.HasDockerfile);
        Assert.False(analysis.HasCompose);
        Assert.Equal(8080, Assert.Single(analysis.Ports).ContainerPort);
        Assert.Equal("/data/nginx/logs", Assert.Single(analysis.Volumes).ContainerPath);
        Assert.Contains(analysis.EnvVars, v => v.Name == "TZ");
        Assert.Contains(analysis.EnvVars, v => v.Name == "ADMIN_PASSWORD");
    }

    // The image config's own baked-in Env includes everything every base
    // layer set (toolchain versions, PATH...), not just what the app itself
    // cares about - a much noisier signal than a single Dockerfile's own ENV
    // lines. These must not turn into addon options.
    [Fact]
    public void BuildRepoAnalysisResult_FiltersOutBaseImageToolchainNoise()
    {
        var inspection = new ImageInspectionResult
        {
            Digest = "sha256:abc",
            RawEnv =
            [
                "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "NODE_VERSION=20.11.0",
                "GOLANG_VERSION=1.22.0",
                "GPG_KEY=1234",
                "TZ=UTC",
            ],
        };

        var analysis = ImageSourceGenerator.BuildRepoAnalysisResult(inspection);

        Assert.Equal(["TZ"], analysis.EnvVars.Select(v => v.Name));
    }

    [Fact]
    public void BuildRepoAnalysisResult_NoEnv_ReturnsEmptyList()
    {
        var analysis = ImageSourceGenerator.BuildRepoAnalysisResult(new ImageInspectionResult { Digest = "sha256:abc" });

        Assert.Empty(analysis.EnvVars);
    }

    [Fact]
    public void BuildSyntheticDockerfile_IncludesFromVolumeEntrypointAndCmd()
    {
        var reference = ImageReference.Parse("example/agent:v1.6.3");
        var inspection = new ImageInspectionResult
        {
            Digest = "sha256:abc",
            Volumes = [new VolumeMapping { ContainerPath = "/etc/agent" }, new VolumeMapping { ContainerPath = "/var/lib/agent/data" }],
            Entrypoint = ["/bin/bash", "/docker_start.sh"],
            Cmd = ["-c", "agent"],
        };

        var dockerfile = ImageSourceGenerator.BuildSyntheticDockerfile(reference, inspection);

        Assert.Contains("FROM example/agent:v1.6.3", dockerfile);
        Assert.Contains("VOLUME [\"/etc/agent\",\"/var/lib/agent/data\"]", dockerfile);
        Assert.Contains("ENTRYPOINT [\"/bin/bash\",\"/docker_start.sh\"]", dockerfile);
        Assert.Contains("CMD [\"-c\",\"agent\"]", dockerfile);
    }

    [Fact]
    public void BuildSyntheticDockerfile_NoVolumesEntrypointOrCmd_OmitsThoseLines()
    {
        var reference = ImageReference.Parse("alpine:3.20");
        var dockerfile = ImageSourceGenerator.BuildSyntheticDockerfile(reference, new ImageInspectionResult { Digest = "sha256:abc" });
        var instructionLines = dockerfile.Split('\n').Where(l => !l.StartsWith('#'));

        Assert.DoesNotContain(instructionLines, l => l.StartsWith("VOLUME", StringComparison.Ordinal));
        Assert.DoesNotContain(instructionLines, l => l.StartsWith("ENTRYPOINT", StringComparison.Ordinal));
        Assert.DoesNotContain(instructionLines, l => l.StartsWith("CMD", StringComparison.Ordinal));
    }

    // The whole point of synthesizing a Dockerfile instead of inventing a
    // parallel code path: EntrypointWrapperGenerator (regex-based, reading
    // Dockerfile text) must parse this synthetic one exactly as it would a
    // real one - this is the end-to-end proof that it actually does.
    [Fact]
    public void BuildSyntheticDockerfile_ParsesCorrectlyThroughEntrypointWrapperGenerator()
    {
        var reference = ImageReference.Parse("example/agent:v1.6.3");
        var inspection = new ImageInspectionResult
        {
            Digest = "sha256:abc",
            Entrypoint = ["/bin/bash", "/docker_start.sh"],
        };

        var dockerfile = ImageSourceGenerator.BuildSyntheticDockerfile(reference, inspection);

        Assert.False(EntrypointWrapperGenerator.FinalStageHasNoShell(dockerfile));

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile);

        Assert.NotNull(script);
        Assert.Contains("exec '/bin/bash' '/docker_start.sh'", script);
    }
}
