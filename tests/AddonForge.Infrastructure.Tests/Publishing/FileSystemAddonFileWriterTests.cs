using AddonForge.Infrastructure.Publishing;
using Microsoft.Extensions.Options;
using Xunit;

namespace AddonForge.Infrastructure.Tests.Publishing;

public class FileSystemAddonFileWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "addon-forge-tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemAddonFileWriter _writer;

    public FileSystemAddonFileWriterTests()
    {
        var options = new FileSystemAddonFileWriterOptions
        {
            StagingRoot = Path.Combine(_root, "staging"),
            PublishedRoot = Path.Combine(_root, "addons"),
            IconsRoot = Path.Combine(_root, "icons"),
        };
        _writer = new FileSystemAddonFileWriter(new StaticOptionsMonitor<FileSystemAddonFileWriterOptions>(options));
    }

    [Fact]
    public async Task WriteToStaging_ThenReadStaged_RoundTrips()
    {
        await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        await _writer.WriteToStagingAsync("my_addon", new Dictionary<string, string> { ["config.yaml"] = "slug: my_addon\n" }, CancellationToken.None);

        var content = await _writer.ReadStagedFileAsync("my_addon", "config.yaml", CancellationToken.None);

        Assert.Equal("slug: my_addon\n", content);
    }

    [Fact]
    public async Task PrepareStaging_CopiesNestedBuildContext_ThenPublishKeepsTheTree()
    {
        var stagingDir = await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);

        // Simulate what AddonGenerationOrchestrator does: IClonedRepo.CopyToAsync
        // drops a whole source tree (with subdirectories) into staging...
        Directory.CreateDirectory(Path.Combine(stagingDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(stagingDir, "requirements.txt"), "flask\n");
        await File.WriteAllTextAsync(Path.Combine(stagingDir, "src", "app.py"), "print('hi')\n");

        // ...and then addon-forge overlays its own generated files on top.
        await _writer.WriteToStagingAsync("my_addon", new Dictionary<string, string> { ["config.yaml"] = "slug: my_addon\n" }, CancellationToken.None);

        await _writer.PublishFromStagingAsync("my_addon", CancellationToken.None);

        var published = await _writer.ListPublishedFilesAsync("my_addon", CancellationToken.None);
        Assert.Contains("requirements.txt", published);
        Assert.Contains("src/app.py", published);
        Assert.Contains("config.yaml", published);
    }

    [Fact]
    public async Task PublishFromStaging_RemovesFilesThatNoLongerExistInTheNewStaging()
    {
        await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        await _writer.WriteToStagingAsync("my_addon", new Dictionary<string, string> { ["old-file.txt"] = "v1\n" }, CancellationToken.None);
        await _writer.PublishFromStagingAsync("my_addon", CancellationToken.None);

        // A fresh generation clears staging first, so a file dropped by the old source version won't reappear.
        await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        await _writer.WriteToStagingAsync("my_addon", new Dictionary<string, string> { ["new-file.txt"] = "v2\n" }, CancellationToken.None);
        await _writer.PublishFromStagingAsync("my_addon", CancellationToken.None);

        var published = await _writer.ListPublishedFilesAsync("my_addon", CancellationToken.None);
        Assert.DoesNotContain("old-file.txt", published);
        Assert.Contains("new-file.txt", published);
    }

    [Fact]
    public async Task PublishFromStaging_NoStagedFiles_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _writer.PublishFromStagingAsync("never_staged", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_RemovesBothStagedAndPublishedFiles()
    {
        await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        await _writer.WriteToStagingAsync("my_addon", new Dictionary<string, string> { ["config.yaml"] = "slug: my_addon\n" }, CancellationToken.None);
        await _writer.PublishFromStagingAsync("my_addon", CancellationToken.None);

        await _writer.DeleteAsync("my_addon", CancellationToken.None);

        Assert.Empty(await _writer.ListStagedFilesAsync("my_addon", CancellationToken.None));
        Assert.Empty(await _writer.ListPublishedFilesAsync("my_addon", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_NothingExists_DoesNotThrow()
    {
        await _writer.DeleteAsync("never_existed", CancellationToken.None);
    }

    [Fact]
    public async Task ReadPublishedFile_BinaryContent_ReturnsNull()
    {
        await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        var stagingDir = Path.Combine(_root, "staging", "my_addon");
        await File.WriteAllBytesAsync(Path.Combine(stagingDir, "icon.bin"), [0x00, 0x01, 0x02, 0x00]);
        await _writer.PublishFromStagingAsync("my_addon", CancellationToken.None);

        var content = await _writer.ReadPublishedFileAsync("my_addon", "icon.bin", CancellationToken.None);

        Assert.Null(content);
    }

    [Fact]
    public async Task ReadPublished_UnknownSlug_ReturnsEmptyListing()
    {
        var result = await _writer.ListPublishedFilesAsync("unknown", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ReadStagedFile_PathEscapingStagingRoot_ReturnsNull()
    {
        await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        await _writer.WriteToStagingAsync("my_addon", new Dictionary<string, string> { ["config.yaml"] = "x\n" }, CancellationToken.None);

        var content = await _writer.ReadStagedFileAsync("my_addon", "../other_addon/config.yaml", CancellationToken.None);

        Assert.Null(content);
    }

    // Only the 8-byte PNG signature is checked by LooksLikeRasterImage - the
    // rest of a real PNG's structure is irrelevant to that check.
    private static readonly byte[] FakePngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    [Fact]
    public async Task EnsureIcon_FindsLogoUnderPublicFolder_CopiesToIconAndLogoPng()
    {
        var stagingDir = await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(stagingDir, "public"));
        await File.WriteAllBytesAsync(Path.Combine(stagingDir, "public", "logo.png"), FakePngBytes);

        await _writer.EnsureIconAsync("my_addon", CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(stagingDir, "icon.png")));
        Assert.True(File.Exists(Path.Combine(stagingDir, "logo.png")));
    }

    [Fact]
    public async Task EnsureIcon_RootIconAlreadyPresent_DoesNotOverwriteIt()
    {
        var stagingDir = await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        var originalBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xAA, 0xBB };
        await File.WriteAllBytesAsync(Path.Combine(stagingDir, "icon.png"), originalBytes);
        Directory.CreateDirectory(Path.Combine(stagingDir, "public"));
        await File.WriteAllBytesAsync(Path.Combine(stagingDir, "public", "logo.png"), FakePngBytes);

        await _writer.EnsureIconAsync("my_addon", CancellationToken.None);

        var iconBytes = await File.ReadAllBytesAsync(Path.Combine(stagingDir, "icon.png"));
        Assert.Equal(originalBytes, iconBytes);
    }

    [Fact]
    public async Task EnsureIcon_SvgFaviconOnly_IsIgnored()
    {
        var stagingDir = await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(stagingDir, "static"));
        await File.WriteAllTextAsync(Path.Combine(stagingDir, "static", "favicon.svg"), "<svg></svg>");

        await _writer.EnsureIconAsync("my_addon", CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(stagingDir, "icon.png")));
    }

    [Fact]
    public async Task EnsureIcon_PngWithoutIconOrLogoInName_IsIgnored()
    {
        // A screenshot dropped in assets/ shouldn't be mistaken for a logo.
        var stagingDir = await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(stagingDir, "assets"));
        await File.WriteAllBytesAsync(Path.Combine(stagingDir, "assets", "preview-screenshot.png"), FakePngBytes);

        await _writer.EnsureIconAsync("my_addon", CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(stagingDir, "icon.png")));
    }

    [Fact]
    public async Task SetManualIcon_NotARealImage_Throws() =>
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _writer.SetManualIconAsync("my_addon", [0x00, 0x01, 0x02], CancellationToken.None));

    [Fact]
    public async Task ApplyManualIcon_OverridesWhateverEnsureIconAutoDetected()
    {
        var stagingDir = await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(stagingDir, "public"));
        await File.WriteAllBytesAsync(Path.Combine(stagingDir, "public", "logo.png"), FakePngBytes);
        await _writer.EnsureIconAsync("my_addon", CancellationToken.None);

        var manualBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xAA, 0xBB };
        await _writer.SetManualIconAsync("my_addon", manualBytes, CancellationToken.None);
        await _writer.ApplyManualIconAsync("my_addon", CancellationToken.None);

        Assert.Equal(manualBytes, await File.ReadAllBytesAsync(Path.Combine(stagingDir, "icon.png")));
        Assert.Equal(manualBytes, await File.ReadAllBytesAsync(Path.Combine(stagingDir, "logo.png")));
    }

    [Fact]
    public async Task HasManualIcon_ReflectsWhetherOneWasSet()
    {
        Assert.False(await _writer.HasManualIconAsync("my_addon", CancellationToken.None));

        await _writer.SetManualIconAsync("my_addon", FakePngBytes, CancellationToken.None);

        Assert.True(await _writer.HasManualIconAsync("my_addon", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_AlsoRemovesTheManualIcon()
    {
        await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        await _writer.SetManualIconAsync("my_addon", FakePngBytes, CancellationToken.None);

        await _writer.DeleteAsync("my_addon", CancellationToken.None);

        Assert.False(await _writer.HasManualIconAsync("my_addon", CancellationToken.None));
    }

    [Fact]
    public async Task ReadPublishedBinaryFile_RoundTrips()
    {
        var stagingDir = await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(stagingDir, "icon.png"), FakePngBytes);
        await _writer.PublishFromStagingAsync("my_addon", CancellationToken.None);

        var bytes = await _writer.ReadPublishedBinaryFileAsync("my_addon", "icon.png", CancellationToken.None);

        Assert.Equal(FakePngBytes, bytes);
    }

    [Fact]
    public async Task ReadPublishedBinaryFile_Missing_ReturnsNull()
    {
        await _writer.PrepareStagingAsync("my_addon", CancellationToken.None);
        await _writer.PublishFromStagingAsync("my_addon", CancellationToken.None);

        var bytes = await _writer.ReadPublishedBinaryFileAsync("my_addon", "icon.png", CancellationToken.None);

        Assert.Null(bytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
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
