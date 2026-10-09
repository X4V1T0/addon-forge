using AddonForge.Core.Abstractions;
using AddonForge.Core.Generation;
using AddonForge.Core.Models;
using AddonForge.Core.Orchestration;
using AddonForge.Core.Versioning;
using AddonForge.Infrastructure.Publishing;
using Microsoft.Extensions.Options;
using Xunit;

namespace AddonForge.Infrastructure.Tests.Orchestration;

/// <summary>
/// Regression coverage for a real bug: creating a branch-tracked addon
/// recorded the literal branch name ("main") as ManagedAddon.CurrentRef
/// instead of resolving it to the branch's actual current commit SHA.
/// Since neither "main" nor a real SHA parses as a semver-ish version,
/// VersionComparer.IsNewer treated every subsequent "Check now" as finding
/// an update, even when the branch hadn't moved at all.
/// </summary>
public class AddonUpdateServiceTests : IDisposable
{
    private const string RepoUrl = "https://example.com/repo.git";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "addon-forge-tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemAddonFileWriter _fileWriter;
    private readonly FakeGitClient _gitClient = new();
    private readonly InMemoryAddonRepository _repository = new();

    public AddonUpdateServiceTests()
    {
        var options = new FileSystemAddonFileWriterOptions
        {
            StagingRoot = Path.Combine(_root, "staging"),
            PublishedRoot = Path.Combine(_root, "addons"),
            IconsRoot = Path.Combine(_root, "icons"),
        };
        _fileWriter = new FileSystemAddonFileWriter(new StaticOptionsMonitor<FileSystemAddonFileWriterOptions>(options));
    }

    private AddonUpdateService BuildService()
    {
        var registryClient = new FakeContainerRegistryClient();
        var orchestrator = new AddonGenerationOrchestrator(_gitClient, new FakeLlmClient(), _fileWriter, registryClient);
        var versionChecker = new VersionCheckService(_gitClient, registryClient);
        return new AddonUpdateService(_repository, orchestrator, versionChecker, _fileWriter, _gitClient);
    }

    private static NewAddonInput BranchInput() => new(
        RepoUrl,
        CloneRef: "main",
        DisplayRef: "main", // what the Web layer passes before resolution - the bug
        Slug: "my_addon",
        Name: "my_addon",
        Description: "desc",
        Version: "0.1.0",
        Ingress: false,
        IngressPort: null);

    [Fact]
    public async Task CreateAsync_BranchTracked_RecordsResolvedShaNotBranchName()
    {
        _gitClient.LatestCommitSha = "1d161d15a4fe93d9b3371377f0a421dc3e965b10";
        var service = BuildService();

        var addon = await service.CreateAsync(BranchInput(), UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);

        Assert.Equal("1d161d15a4fe93d9b3371377f0a421dc3e965b10", addon.CurrentRef);
        Assert.NotEqual("main", addon.CurrentRef);
    }

    [Fact]
    public async Task CheckAndRegenerateAsync_BranchHasNotMovedSinceCreate_DoesNotFlagAnUpdate()
    {
        _gitClient.LatestCommitSha = "1d161d15a4fe93d9b3371377f0a421dc3e965b10";
        var service = BuildService();
        await service.CreateAsync(BranchInput(), UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);

        // Branch hasn't moved - GetLatestCommitShaAsync keeps returning the same SHA.
        var result = await service.CheckAndRegenerateAsync("my_addon", CancellationToken.None);

        Assert.Null(result.LastError);
        Assert.Null(result.StagedRef);
        Assert.Equal("0.1.0", result.CurrentVersion);
    }

    [Fact]
    public async Task CheckAndRegenerateAsync_BranchMoved_FlagsUpdateAndBumpsPatch()
    {
        _gitClient.LatestCommitSha = "1d161d15a4fe93d9b3371377f0a421dc3e965b10";
        var service = BuildService();
        await service.CreateAsync(BranchInput(), UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);

        _gitClient.LatestCommitSha = "2222222222222222222222222222222222222222";
        var result = await service.CheckAndRegenerateAsync("my_addon", CancellationToken.None);

        Assert.Null(result.LastError);
        Assert.Equal("2222222222222222222222222222222222222222", result.StagedRef);
        Assert.Equal("0.1.1", result.StagedVersion);
    }

    [Fact]
    public async Task MapEntries_PersistAcrossRegeneration()
    {
        // addon-forge can't reliably guess which host folder a container path
        // like /music is supposed to be - a manually chosen `map:` entry must
        // survive every future regeneration, not just the initial create.
        _gitClient.LatestCommitSha = "1d161d15a4fe93d9b3371377f0a421dc3e965b10";
        var service = BuildService();
        var input = BranchInput() with { MapEntries = ["media:rw"] };

        var created = await service.CreateAsync(input, UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);
        Assert.Equal(["media:rw"], created.MapEntries);

        _gitClient.LatestCommitSha = "2222222222222222222222222222222222222222";
        var result = await service.CheckAndRegenerateAsync("my_addon", CancellationToken.None);
        await service.PublishStagedAsync("my_addon", CancellationToken.None);

        Assert.Equal(["media:rw"], result.MapEntries);
        var configYaml = await _fileWriter.ReadPublishedFileAsync("my_addon", "config.yaml", CancellationToken.None);
        Assert.Contains("media:rw", configYaml);
    }

    [Fact]
    public async Task ManualEnvVars_PersistAcrossRegeneration()
    {
        // Same rationale as MapEntries: env vars a repo only documents in
        // prose (README/wiki), not in its Dockerfile/compose/.env.example,
        // have to survive every future regeneration too, not just create.
        _gitClient.LatestCommitSha = "1d161d15a4fe93d9b3371377f0a421dc3e965b10";
        var service = BuildService();
        var input = BranchInput() with { ManualEnvVars = ["ADMIN_PASSWORD=changeme"] };

        var created = await service.CreateAsync(input, UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);
        Assert.Equal(["ADMIN_PASSWORD=changeme"], created.ManualEnvVars);
        var initialYaml = await _fileWriter.ReadPublishedFileAsync("my_addon", "config.yaml", CancellationToken.None);
        Assert.Contains("ADMIN_PASSWORD", initialYaml);

        _gitClient.LatestCommitSha = "2222222222222222222222222222222222222222";
        var result = await service.CheckAndRegenerateAsync("my_addon", CancellationToken.None);
        await service.PublishStagedAsync("my_addon", CancellationToken.None);

        Assert.Equal(["ADMIN_PASSWORD=changeme"], result.ManualEnvVars);
        var configYaml = await _fileWriter.ReadPublishedFileAsync("my_addon", "config.yaml", CancellationToken.None);
        Assert.Contains("ADMIN_PASSWORD", configYaml);
    }

    [Fact]
    public async Task RegenerateAsync_AppliesManualEnvVarsImmediatelyWithoutVersionBump()
    {
        // Editing extra env vars on an already-created addon shouldn't wait
        // for the next real upstream update to actually land in config.yaml.
        var service = BuildService();
        var addon = await service.CreateAsync(BranchInput(), UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);
        addon.ManualEnvVars = ["ADMIN_PASSWORD=changeme"];

        var result = await service.RegenerateAsync("my_addon", CancellationToken.None);

        Assert.Equal("0.1.0", result.CurrentVersion);
        var configYaml = await _fileWriter.ReadPublishedFileAsync("my_addon", "config.yaml", CancellationToken.None);
        Assert.Contains("ADMIN_PASSWORD", configYaml);
    }

    [Fact]
    public async Task ConfigFileTemplate_PersistsAcrossRegeneration()
    {
        // Same rationale as MapEntries/ManualEnvVars: a config file the app
        // only reads from disk, not env vars, has to survive every future
        // regeneration too, not just create. Needs a real ENTRYPOINT in the
        // fake Dockerfile - unlike options (always written to config.yaml
        // regardless), the rendered template is only ever written when the
        // entrypoint could actually be wrapped.
        _gitClient.RootDockerfileContent = "FROM alpine\nENTRYPOINT [\"/app\"]\n";
        _gitClient.LatestCommitSha = "1d161d15a4fe93d9b3371377f0a421dc3e965b10";
        var service = BuildService();
        var input = BranchInput() with
        {
            ConfigFileTemplate = "weather:\n  location: ${WEATHER_LOCATION}\n",
            ConfigFileDestPath = "/config/config.yaml",
        };

        var created = await service.CreateAsync(input, UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);
        Assert.Equal("/config/config.yaml", created.ConfigFileDestPath);
        var initialTemplate = await _fileWriter.ReadPublishedFileAsync(
            "my_addon", EntrypointWrapperGenerator.ConfigTemplateFileName, CancellationToken.None);
        Assert.Contains("WEATHER_LOCATION", initialTemplate);

        _gitClient.LatestCommitSha = "2222222222222222222222222222222222222222";
        var result = await service.CheckAndRegenerateAsync("my_addon", CancellationToken.None);
        await service.PublishStagedAsync("my_addon", CancellationToken.None);

        Assert.Equal("/config/config.yaml", result.ConfigFileDestPath);
        var template = await _fileWriter.ReadPublishedFileAsync(
            "my_addon", EntrypointWrapperGenerator.ConfigTemplateFileName, CancellationToken.None);
        Assert.Contains("WEATHER_LOCATION", template);
    }

    [Fact]
    public async Task ConfigFileTemplate_PlaceholderNotAlreadyAnOption_IsAutoExposedAsOne()
    {
        // Reported bug: a repo with no other options at all still needs
        // ${WEATHER_LOCATION} to show up in the addon's own Configuration
        // tab just from being referenced in the config file template -
        // otherwise there's no way to actually set it, since it was never
        // separately detected or added as an extra env var.
        _gitClient.RootDockerfileContent = "FROM alpine\nENTRYPOINT [\"/app\"]\n";
        var service = BuildService();
        var input = BranchInput() with
        {
            ConfigFileTemplate = "weather:\n  location: ${WEATHER_LOCATION}\n",
            ConfigFileDestPath = "/config/config.yaml",
        };

        await service.CreateAsync(input, UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);

        var configYaml = await _fileWriter.ReadPublishedFileAsync("my_addon", "config.yaml", CancellationToken.None);
        Assert.Contains("WEATHER_LOCATION", configYaml);
    }

    [Fact]
    public async Task RegenerateAsync_AppliesConfigFileTemplateImmediatelyWithoutVersionBump()
    {
        _gitClient.RootDockerfileContent = "FROM alpine\nENTRYPOINT [\"/app\"]\n";
        var service = BuildService();
        var addon = await service.CreateAsync(BranchInput(), UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);
        addon.ConfigFileTemplate = "weather:\n  location: ${WEATHER_LOCATION}\n";
        addon.ConfigFileDestPath = "/config/config.yaml";

        var result = await service.RegenerateAsync("my_addon", CancellationToken.None);

        Assert.Equal("0.1.0", result.CurrentVersion);
        var template = await _fileWriter.ReadPublishedFileAsync(
            "my_addon", EntrypointWrapperGenerator.ConfigTemplateFileName, CancellationToken.None);
        Assert.Contains("WEATHER_LOCATION", template);
    }

    [Fact]
    public async Task AllowDistrolessDebugShell_PersistsAcrossRegenerationAndRewritesDockerfile()
    {
        _gitClient.RootDockerfileContent =
            "FROM gcr.io/distroless/static-debian13:nonroot\nENTRYPOINT [\"/app\"]\n";
        _gitClient.LatestCommitSha = "1d161d15a4fe93d9b3371377f0a421dc3e965b10";
        var service = BuildService();
        var input = BranchInput() with
        {
            ManualEnvVars = ["ADMIN_PASSWORD=changeme"],
            AllowDistrolessDebugShell = true,
        };

        var created = await service.CreateAsync(input, UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);
        Assert.True(created.AllowDistrolessDebugShell);
        var initialDockerfile = await _fileWriter.ReadPublishedFileAsync("my_addon", "Dockerfile", CancellationToken.None);
        Assert.Contains(":debug-nonroot", initialDockerfile);

        _gitClient.LatestCommitSha = "2222222222222222222222222222222222222222";
        var result = await service.CheckAndRegenerateAsync("my_addon", CancellationToken.None);
        await service.PublishStagedAsync("my_addon", CancellationToken.None);

        Assert.True(result.AllowDistrolessDebugShell);
        var dockerfile = await _fileWriter.ReadPublishedFileAsync("my_addon", "Dockerfile", CancellationToken.None);
        Assert.Contains(":debug-nonroot", dockerfile);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFilesAndStopsTracking()
    {
        var service = BuildService();
        await service.CreateAsync(BranchInput(), UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);
        Assert.NotEmpty(await _fileWriter.ListPublishedFilesAsync("my_addon", CancellationToken.None));

        await service.DeleteAsync("my_addon", CancellationToken.None);

        Assert.Empty(await _fileWriter.ListPublishedFilesAsync("my_addon", CancellationToken.None));
        Assert.Null(await _repository.GetBySlugAsync("my_addon", CancellationToken.None));
    }

    // Regression coverage for a second real bug: a repo with no Dockerfile at
    // its root (multi-service stacks often ship one per component instead,
    // e.g. docker/Dockerfile.aio, docker/Dockerfile.controller...) silently
    // produced an addon missing its Dockerfile entirely - the Supervisor then
    // failed the install with a bare "dockerfile is missing", no context.
    // Generation now either lets the caller point at one of the other
    // Dockerfiles found, or fails immediately with a clear reason.
    [Fact]
    public async Task CreateAsync_NoRootDockerfileAndNoPathChosen_ThrowsWithClearReason()
    {
        _gitClient.RootDockerfileContent = null;
        _gitClient.DockerfileCandidates = ["docker/Dockerfile.aio", "docker/Dockerfile.controller"];
        var service = BuildService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(BranchInput(), UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None));

        Assert.Contains("Dockerfile.aio", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_NoRootDockerfileButPathChosen_UsesThatFilesContent()
    {
        _gitClient.RootDockerfileContent = null;
        _gitClient.DockerfileCandidates = ["docker/Dockerfile.aio"];
        _gitClient.Files["docker/Dockerfile.aio"] = "FROM node:22-bookworm-slim\n";
        var service = BuildService();

        var input = BranchInput() with { DockerfilePath = "docker/Dockerfile.aio" };
        var addon = await service.CreateAsync(input, UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);

        Assert.Equal("docker/Dockerfile.aio", addon.DockerfilePath);
        var published = await _fileWriter.ReadPublishedFileAsync("my_addon", "Dockerfile", CancellationToken.None);
        Assert.Equal("FROM node:22-bookworm-slim\n", published);
    }

    /// <summary>
    /// Regression: an allowlist-style .dockerignore (`*` then `!src/`) kept
    /// the generated addon-forge-entrypoint.sh out of the build context, and
    /// the Supervisor build failed with "addon-forge-entrypoint.sh: not found".
    /// </summary>
    [Fact]
    public async Task CreateAsync_AllowlistDockerignore_ReincludesGeneratedFiles()
    {
        _gitClient.RootDockerfileContent = "FROM alpine\nENV API_TOKEN=changeme\nENTRYPOINT [\"/app\"]\n";
        _gitClient.Files[".dockerignore"] = "*\n!src/\n";
        var service = BuildService();

        await service.CreateAsync(BranchInput(), UpdatePolicy.Manual, SourceRefType.Branch, "main", CancellationToken.None);

        var published = await _fileWriter.ReadPublishedFileAsync("my_addon", ".dockerignore", CancellationToken.None);
        Assert.NotNull(published);
        Assert.StartsWith("*\n!src/\n", published);
        Assert.EndsWith("\n!addon-forge-*\n", published);
        Assert.Null(await _fileWriter.ReadPublishedFileAsync("my_addon", "Dockerfile.dockerignore", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeGitClient : IGitClient
    {
        public string LatestCommitSha { get; set; } = "0000000000000000000000000000000000000000";

        public string? LatestTag { get; set; }

        /// <summary>Root Dockerfile content the next clone should report - a minimal one by default, null to simulate "no root Dockerfile".</summary>
        public string? RootDockerfileContent { get; set; } = "FROM alpine\n";

        public IReadOnlyList<string> DockerfileCandidates { get; set; } = [];

        public Dictionary<string, string> Files { get; } = [];

        public Task<IClonedRepo> ShallowCloneAsync(string repoUrl, string refName, CancellationToken ct) =>
            Task.FromResult<IClonedRepo>(new FakeClonedRepo(RootDockerfileContent, DockerfileCandidates, Files));

        public Task<string?> GetLatestTagAsync(string repoUrl, CancellationToken ct) => Task.FromResult(LatestTag);

        public Task<string> GetLatestCommitShaAsync(string repoUrl, string branch, CancellationToken ct) =>
            Task.FromResult(LatestCommitSha);
    }

    private sealed class FakeClonedRepo(string? rootDockerfileContent, IReadOnlyList<string> candidates, Dictionary<string, string> files) : IClonedRepo
    {
        public string Path { get; } = System.IO.Path.GetTempPath();

        public RepoSourceFiles ReadSourceFiles() => new() { DockerfileContent = rootDockerfileContent };

        public Task CopyToAsync(string destinationDirectory, CancellationToken ct)
        {
            Directory.CreateDirectory(destinationDirectory);
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> FindDockerfileCandidates() => candidates;

        public string? ReadFile(string relativePath) => files.GetValueOrDefault(relativePath);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Should not be called - this fixture has no ambiguous env vars.");
    }

    /// <summary>Every fixture in this file is git-sourced - see ImageSourceGeneratorTests/AddonGenerationOrchestrator's own image-source coverage for that path instead.</summary>
    private sealed class FakeContainerRegistryClient : IContainerRegistryClient
    {
        public Task<ImageInspectionResult> InspectAsync(ImageReference reference, CancellationToken ct) =>
            throw new InvalidOperationException("Should not be called - every fixture here is git-sourced.");

        public Task<IReadOnlyList<string>> ListTagsAsync(ImageReference reference, CancellationToken ct) =>
            throw new InvalidOperationException("Should not be called - every fixture here is git-sourced.");

        public Task<string?> GetLatestTagAsync(ImageReference reference, CancellationToken ct) =>
            throw new InvalidOperationException("Should not be called - every fixture here is git-sourced.");
    }

    private sealed class InMemoryAddonRepository : IManagedAddonRepository
    {
        private readonly Dictionary<string, ManagedAddon> _addons = [];

        public Task<IReadOnlyList<ManagedAddon>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedAddon>>([.. _addons.Values]);

        public Task<ManagedAddon?> GetBySlugAsync(string slug, CancellationToken ct) =>
            Task.FromResult(_addons.GetValueOrDefault(slug));

        public Task AddAsync(ManagedAddon addon, CancellationToken ct)
        {
            _addons[addon.Slug] = addon;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ManagedAddon addon, CancellationToken ct)
        {
            _addons[addon.Slug] = addon;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string slug, CancellationToken ct)
        {
            _addons.Remove(slug);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
