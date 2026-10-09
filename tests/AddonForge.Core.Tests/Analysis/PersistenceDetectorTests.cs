using AddonForge.Core.Analysis;
using AddonForge.Core.Models;
using Xunit;

namespace AddonForge.Core.Tests.Analysis;

public class PersistenceDetectorTests
{
    // Modelled on the real subwave AIO Dockerfile that lost its data on an update.
    private const string SubwaveLike = """
        FROM node:22-bookworm-slim
        WORKDIR /app
        COPY controller/ /app/
        ENV NODE_ENV=production STATE_DIR=/var/sub-wave SOUNDS_DIR=/sounds
        RUN mkdir -p /var/sub-wave && \
            chmod 777 /var/sub-wave
        ENTRYPOINT ["/usr/local/bin/subwave-supervisor"]
        """;

    [Fact]
    public void Detect_FindsHardcodedStateDirFromEnvAndMkdir()
    {
        var result = PersistenceDetector.Detect(SubwaveLike, []);

        var state = Assert.Single(result, c => c.Path == "/var/sub-wave");
        Assert.True(state.Recommended);
    }

    [Fact]
    public void Detect_DoesNotFlagAppCodeOrUnrelatedEnvDirs()
    {
        var result = PersistenceDetector.Detect(SubwaveLike, []);

        Assert.DoesNotContain(result, c => c.Path == "/app");
        Assert.DoesNotContain(result, c => c.Path == "/sounds");
    }

    [Fact]
    public void Detect_DockerfileVolume_IsRecommended()
    {
        var result = PersistenceDetector.Detect("FROM alpine\nVOLUME [\"/config\", \"/var/lib/app\"]\n", []);

        Assert.All(result, c => Assert.True(c.Recommended));
        Assert.Equal(["/config", "/var/lib/app"], result.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Detect_ComposeVolumes_SkipsReadOnlyAndSystemPaths()
    {
        var result = PersistenceDetector.Detect(null,
        [
            new VolumeMapping { ContainerPath = "/srv/db" },
            new VolumeMapping { ContainerPath = "/etc/localtime", ReadOnly = false },
            new VolumeMapping { ContainerPath = "/var/run/docker.sock" },
            new VolumeMapping { ContainerPath = "/config", ReadOnly = true },
            new VolumeMapping { ContainerPath = "/data" },
            new VolumeMapping { ContainerPath = "/app/settings.yml" },
        ]);

        Assert.Equal(["/srv/db"], result.Select(c => c.Path));
    }

    [Fact]
    public void Detect_WeakEnvNames_AreListedButNotRecommended()
    {
        var result = PersistenceDetector.Detect("FROM alpine\nENV CONFIG_DIR=/etc/myapp\n", []);

        var c = Assert.Single(result);
        Assert.Equal("/etc/myapp", c.Path);
        Assert.False(c.Recommended);
    }

    [Fact]
    public void Detect_UserContentLibraryVolume_IsListedButNotRecommended()
    {
        // Navidrome: VOLUME ["/data", "/music"], ENV ND_MUSICFOLDER=/music
        var files = new RepoSourceFiles
        {
            DockerfileContent = "FROM alpine\nENV ND_MUSICFOLDER=/music ND_DATAFOLDER=/data\nVOLUME [\"/data\", \"/music\"]\n",
        };

        var result = RepoAnalyzer.Analyze(files).PersistentPathCandidates;

        var music = Assert.Single(result);
        Assert.Equal("/music", music.Path);
        Assert.False(music.Recommended);
        Assert.Contains("ND_MUSICFOLDER", music.Reason);
    }

    [Fact]
    public void Detect_PathHoldingTheAppsOwnCode_IsNeverGuessed()
    {
        const string dockerfile = "FROM alpine\nWORKDIR /srv/app\nCOPY . /srv/app\nENV DATA_DIR=/srv/app\nRUN mkdir -p /srv/app && chmod 777 /srv/app\n";

        Assert.Empty(PersistenceDetector.Detect(dockerfile, []));
    }

    [Fact]
    public void Detect_MkdirWithoutChmodOrChown_IsNotASignal()
    {
        Assert.Empty(PersistenceDetector.Detect("FROM alpine\nRUN mkdir -p /opt/tools/bin\n", []));
    }

    [Fact]
    public void NormalizePaths_DropsUnsafeNestedAndDuplicatePaths()
    {
        var result = PersistenceDetector.NormalizePaths(
            ["/var/lib/app/", "/var/lib/app", "/var/lib/app/sub", "relative/path", "/", "/data/x", "/proc/1", "  /srv/db  ", "/etc/config.yml"]);

        Assert.Equal(["/srv/db", "/var/lib/app"], result);
    }

    [Fact]
    public void TargetFor_MapsIntoDataPersist()
    {
        Assert.Equal("/data/persist/var_sub-wave", PersistenceDetector.TargetFor("/var/sub-wave"));
    }

    [Fact]
    public void SharedTargetFor_MapsIntoShareNamespacedBySlug()
    {
        Assert.Equal("/share/proxy/nginx_logs", PersistenceDetector.SharedTargetFor("proxy", "/nginx/logs"));
    }

    // Two addons that each share a path with the same last segment (e.g. both
    // have a "logs" dir) must not collide in the one /share folder every
    // addon can see - the slug namespace is what prevents that.
    [Fact]
    public void SharedTargetFor_DifferentSlugsForSamePath_DoNotCollide()
    {
        Assert.NotEqual(
            PersistenceDetector.SharedTargetFor("proxy", "/data/nginx/logs"),
            PersistenceDetector.SharedTargetFor("agent", "/data/nginx/logs"));
    }

    // Regression coverage for a real bug: `/usr` was forbidden as a whole
    // subtree, so a legitimate data directory under it - geodata's own
    // `VOLUME /usr/share/geodata` - was silently dropped, and the addon came
    // out with no shared path at all (the only trace was a small "1 entry
    // was ignored" note). Only the system binary/library directories, and
    // the top-level /usr, /usr/local and /usr/share themselves, are off limits.
    [Theory]
    [InlineData("/usr/share/geodata")]
    [InlineData("/usr/share/nginx/html")]
    [InlineData("/usr/local/apache2/logs")]
    public void NormalizePaths_DataDirectoryUnderUsr_IsAccepted(string path)
    {
        Assert.Equal([path], PersistenceDetector.NormalizePaths([path]));
        Assert.Equal([path], PersistenceDetector.NormalizePaths([path], allowUnderData: true));
    }

    [Theory]
    [InlineData("/usr")]
    [InlineData("/usr/local")]
    [InlineData("/usr/share")]
    [InlineData("/usr/bin")]
    [InlineData("/usr/bin/node")]
    [InlineData("/usr/sbin")]
    [InlineData("/usr/lib")]
    [InlineData("/usr/lib64/x86_64")]
    [InlineData("/usr/local/bin")]
    public void NormalizePaths_SystemDirectoriesUnderUsr_StayRejected(string path)
    {
        Assert.Empty(PersistenceDetector.NormalizePaths([path]));
    }

    [Fact]
    public void NormalizePaths_RejectsShareItself()
    {
        Assert.Empty(PersistenceDetector.NormalizePaths(["/share", "/share/sub"]));
    }
}
