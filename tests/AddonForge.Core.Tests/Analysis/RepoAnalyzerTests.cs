using AddonForge.Core.Analysis;
using AddonForge.Core.Models;
using Xunit;

namespace AddonForge.Core.Tests.Analysis;

public class RepoAnalyzerTests
{
    [Fact]
    public void Analyze_ComposeEnvVar_OverridesDockerfileDefaultForSameName()
    {
        var files = new RepoSourceFiles
        {
            DockerfileContent = "FROM alpine\nENV LOG_LEVEL=debug\n",
            ComposeContent = """
                services:
                  app:
                    build: .
                    environment:
                      - LOG_LEVEL=info
                """,
        };

        var result = RepoAnalyzer.Analyze(files);

        var logLevel = Assert.Single(result.EnvVars, v => v.Name == "LOG_LEVEL");
        Assert.Equal("info", logLevel.DefaultValue);
    }

    [Fact]
    public void Analyze_PortsFromBothSources_AreMergedWithoutDuplicates()
    {
        var files = new RepoSourceFiles
        {
            DockerfileContent = "FROM alpine\nEXPOSE 80\nEXPOSE 443\n",
            ComposeContent = """
                services:
                  app:
                    build: .
                    ports:
                      - "8080:80"
                """,
        };

        var result = RepoAnalyzer.Analyze(files);

        Assert.Equal(2, result.Ports.Count);
        Assert.Contains(result.Ports, p => p.ContainerPort == 80 && p.SuggestedHostPort == 8080);
        Assert.Contains(result.Ports, p => p.ContainerPort == 443);
    }

    [Fact]
    public void Analyze_AmbiguousEnvVars_AreExposedSeparately()
    {
        var files = new RepoSourceFiles
        {
            DockerfileContent = "FROM alpine\nENV MODE=\nENV TZ=Etc/UTC\n",
        };

        var result = RepoAnalyzer.Analyze(files);

        Assert.Contains(result.AmbiguousEnvVars, v => v.Name == "MODE");
        Assert.DoesNotContain(result.AmbiguousEnvVars, v => v.Name == "TZ");
    }

    [Fact]
    public void Analyze_NoFiles_ReturnsEmptyResultWithoutThrowing()
    {
        var result = RepoAnalyzer.Analyze(new RepoSourceFiles());

        Assert.False(result.HasDockerfile);
        Assert.False(result.HasCompose);
        Assert.Empty(result.Ports);
        Assert.Empty(result.EnvVars);
    }

    [Fact]
    public void Analyze_ReadmeExcerpt_SkipsTitleHeadingInFavorOfRealDescription()
    {
        // Matches the common README shape: an H1 that just repeats the
        // project name, then the actual description - the heading alone
        // isn't useful as a description since it duplicates the addon Name.
        var files = new RepoSourceFiles { ReadmeContent = "# My Project\nA short description.\n" };

        var result = RepoAnalyzer.Analyze(files);

        Assert.Equal("A short description.", result.ReadmeExcerpt);
    }

    [Fact]
    public void Analyze_ReadmeExcerpt_OnlyHeadingPresent_FallsBackToIt()
    {
        var files = new RepoSourceFiles { ReadmeContent = "# My Project\n" };

        var result = RepoAnalyzer.Analyze(files);

        Assert.Equal("My Project", result.ReadmeExcerpt);
    }

    [Fact]
    public void Analyze_ReadmeExcerpt_SkipsLeadingImageAndBadgeLines()
    {
        var files = new RepoSourceFiles
        {
            ReadmeContent = "# ReClip\n\n" +
                             "A self-hosted video downloader with a clean web UI.\n\n" +
                             "![Python](https://img.shields.io/badge/python-3.8+-blue)\n" +
                             "![preview](assets/preview.png)\n",
        };

        var result = RepoAnalyzer.Analyze(files);

        Assert.Equal("A self-hosted video downloader with a clean web UI.", result.ReadmeExcerpt);
    }
}
