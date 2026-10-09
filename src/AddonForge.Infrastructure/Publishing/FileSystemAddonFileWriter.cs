using AddonForge.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace AddonForge.Infrastructure.Publishing;

/// <summary>
/// Plain filesystem I/O against two roots: staging (addon-forge's own working
/// area, holding a full addon build context) and published (the real local
/// addon store, reached via the `addons:rw` map). No Supervisor API call ever
/// happens here — once files land under the published root, the user
/// refreshes the store from HA themselves.
/// </summary>
public class FileSystemAddonFileWriter(IOptionsMonitor<FileSystemAddonFileWriterOptions> options) : IAddonFileWriter
{
    /// <summary>Files above this size aren't read for preview — likely not something worth rendering as text anyway.</summary>
    private const long MaxPreviewBytes = 256 * 1024;

    public Task<string> PrepareStagingAsync(string slug, CancellationToken ct)
    {
        var dir = Path.Combine(options.CurrentValue.StagingRoot, slug);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        return Task.FromResult(dir);
    }

    public async Task WriteToStagingAsync(string slug, IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var dir = Path.Combine(options.CurrentValue.StagingRoot, slug);
        Directory.CreateDirectory(dir);

        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(dir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content, ct);
        }
    }

    public Task PublishFromStagingAsync(string slug, CancellationToken ct)
    {
        var stagingDir = Path.Combine(options.CurrentValue.StagingRoot, slug);
        if (!Directory.Exists(stagingDir))
        {
            throw new InvalidOperationException($"No staged files for '{slug}'.");
        }

        var publishedDir = Path.Combine(options.CurrentValue.PublishedRoot, slug);
        if (Directory.Exists(publishedDir))
        {
            Directory.Delete(publishedDir, recursive: true);
        }

        CopyDirectory(stagingDir, publishedDir);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListStagedFilesAsync(string slug, CancellationToken ct) =>
        Task.FromResult(ListFiles(Path.Combine(options.CurrentValue.StagingRoot, slug)));

    public Task<IReadOnlyList<string>> ListPublishedFilesAsync(string slug, CancellationToken ct) =>
        Task.FromResult(ListFiles(Path.Combine(options.CurrentValue.PublishedRoot, slug)));

    public Task<string?> ReadStagedFileAsync(string slug, string relativePath, CancellationToken ct) =>
        ReadFileAsync(Path.Combine(options.CurrentValue.StagingRoot, slug), relativePath, ct);

    public Task<string?> ReadPublishedFileAsync(string slug, string relativePath, CancellationToken ct) =>
        ReadFileAsync(Path.Combine(options.CurrentValue.PublishedRoot, slug), relativePath, ct);

    public Task EnsureIconAsync(string slug, CancellationToken ct)
    {
        var dir = Path.Combine(options.CurrentValue.StagingRoot, slug);
        if (!Directory.Exists(dir))
        {
            return Task.CompletedTask;
        }

        var iconPath = Path.Combine(dir, "icon.png");
        var logoPath = Path.Combine(dir, "logo.png");
        var hasIcon = File.Exists(iconPath) && LooksLikeRasterImage(iconPath);
        var hasLogo = File.Exists(logoPath) && LooksLikeRasterImage(logoPath);
        if (hasIcon && hasLogo)
        {
            return Task.CompletedTask;
        }

        var candidate = FindIconCandidate(dir);
        if (candidate is null)
        {
            return Task.CompletedTask;
        }

        if (!hasIcon)
        {
            File.Copy(candidate, iconPath, overwrite: true);
        }

        if (!hasLogo)
        {
            File.Copy(candidate, logoPath, overwrite: true);
        }

        return Task.CompletedTask;
    }

    public async Task SetManualIconAsync(string slug, byte[] imageBytes, CancellationToken ct)
    {
        if (!LooksLikeRasterImage(imageBytes))
        {
            throw new InvalidOperationException("That file doesn't look like a PNG or JPEG image.");
        }

        Directory.CreateDirectory(options.CurrentValue.IconsRoot);
        await File.WriteAllBytesAsync(ManualIconPath(slug), imageBytes, ct);
    }

    public Task<bool> HasManualIconAsync(string slug, CancellationToken ct) =>
        Task.FromResult(File.Exists(ManualIconPath(slug)));

    public Task ApplyManualIconAsync(string slug, CancellationToken ct)
    {
        var manualPath = ManualIconPath(slug);
        var dir = Path.Combine(options.CurrentValue.StagingRoot, slug);
        if (!File.Exists(manualPath) || !Directory.Exists(dir))
        {
            return Task.CompletedTask;
        }

        File.Copy(manualPath, Path.Combine(dir, "icon.png"), overwrite: true);
        File.Copy(manualPath, Path.Combine(dir, "logo.png"), overwrite: true);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReadPublishedBinaryFileAsync(string slug, string relativePath, CancellationToken ct)
    {
        var root = Path.Combine(options.CurrentValue.PublishedRoot, slug);
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        // Guard against a relative path escaping the addon's own folder.
        if (!Path.GetFullPath(fullPath).StartsWith(Path.GetFullPath(root), StringComparison.Ordinal) ||
            !File.Exists(fullPath))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(fullPath, ct);
    }

    public Task DeleteAsync(string slug, CancellationToken ct)
    {
        var stagingDir = Path.Combine(options.CurrentValue.StagingRoot, slug);
        var publishedDir = Path.Combine(options.CurrentValue.PublishedRoot, slug);
        var manualIconPath = ManualIconPath(slug);

        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, recursive: true);
        }

        if (Directory.Exists(publishedDir))
        {
            Directory.Delete(publishedDir, recursive: true);
        }

        if (File.Exists(manualIconPath))
        {
            File.Delete(manualIconPath);
        }

        return Task.CompletedTask;
    }

    private string ManualIconPath(string slug) => Path.Combine(options.CurrentValue.IconsRoot, $"{slug}.img");

    private static readonly string[] IconCandidateRelativePaths =
    [
        "icon.png", "logo.png",
        "public/icon.png", "public/logo.png",
        "static/icon.png", "static/logo.png",
        "assets/icon.png", "assets/logo.png",
        ".github/icon.png", ".github/logo.png",
        "docs/icon.png", "docs/logo.png",
        "favicon.png", "public/favicon.png", "static/favicon.png", "assets/favicon.png",
    ];

    private static readonly string[] IconScanDirectories = ["", "public", "assets", "static", ".github", "docs"];

    /// <summary>
    /// Best-effort search for a usable icon/logo image in the repo. Only ever
    /// picks up raster images (PNG/JPEG) it can verify by file signature -
    /// SVG-only logos (common for favicons) aren't converted, since that
    /// would need an image rendering dependency this addon doesn't carry.
    /// </summary>
    private static string? FindIconCandidate(string root)
    {
        foreach (var relative in IconCandidateRelativePaths)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full) && LooksLikeRasterImage(full))
            {
                return full;
            }
        }

        foreach (var subdir in IconScanDirectories)
        {
            var dir = subdir.Length == 0 ? root : Path.Combine(root, subdir);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var match = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => IsLikelyIconName(Path.GetFileNameWithoutExtension(f)))
                .FirstOrDefault(LooksLikeRasterImage);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static bool IsLikelyIconName(string nameWithoutExtension) =>
        nameWithoutExtension.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
        nameWithoutExtension.Contains("logo", StringComparison.OrdinalIgnoreCase);

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    private static bool LooksLikeRasterImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[8];
            var read = stream.Read(header, 0, header.Length);
            return LooksLikeRasterImage(header.AsSpan(0, read));
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool LooksLikeRasterImage(ReadOnlySpan<byte> header) =>
        (header.Length >= PngSignature.Length && header[..PngSignature.Length].SequenceEqual(PngSignature)) ||
        (header.Length >= JpegSignature.Length && header[..JpegSignature.Length].SequenceEqual(JpegSignature));

    private static IReadOnlyList<string> ListFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)];
    }

    private static async Task<string?> ReadFileAsync(string root, string relativePath, CancellationToken ct)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        // Guard against a relative path escaping the addon's own folder.
        if (!Path.GetFullPath(fullPath).StartsWith(Path.GetFullPath(root), StringComparison.Ordinal))
        {
            return null;
        }

        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length > MaxPreviewBytes)
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        return LooksLikeText(bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        var sampleLength = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < sampleLength; i++)
        {
            if (bytes[i] == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var entry in new DirectoryInfo(sourceDir).EnumerateFileSystemInfos())
        {
            var destPath = Path.Combine(destinationDir, entry.Name);
            if (entry is DirectoryInfo dir)
            {
                CopyDirectory(dir.FullName, destPath);
            }
            else
            {
                File.Copy(entry.FullName, destPath, overwrite: true);
            }
        }
    }
}
