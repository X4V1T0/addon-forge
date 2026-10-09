using System.Diagnostics;
using System.Text.RegularExpressions;
using AddonForge.Core.Abstractions;
using AddonForge.Core.Models;
using Microsoft.Extensions.Options;

namespace AddonForge.Infrastructure.Git;

/// <summary>
/// Shells out to the `git` binary rather than using LibGit2Sharp — a plain
/// `git clone --depth 1` is simpler and avoids pulling in native bindings for
/// something the CLI already does reliably.
/// </summary>
public class GitCliClient(IOptionsMonitor<GitOptions> options) : IGitClient
{
    public async Task<IClonedRepo> ShallowCloneAsync(string repoUrl, string refName, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "addon-forge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        // Force LF-preserving checkout regardless of the host's git config -
        // a local `core.autocrlf=true` (a common Windows default) would
        // otherwise corrupt any script file in the source repo with CRLF
        // line endings, breaking its shebang line at container runtime.
        await RunGitAsync(
            ["-c", "core.autocrlf=false", "clone", "--depth", "1", "--branch", refName, "--single-branch",
                ApplyAuth(repoUrl), tempPath],
            ct);

        return new ClonedRepo(tempPath);
    }

    public async Task<string?> GetLatestTagAsync(string repoUrl, CancellationToken ct)
    {
        var output = await RunGitAsync(["ls-remote", "--tags", "--refs", "--sort=-v:refname", ApplyAuth(repoUrl)], ct);
        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstLine is null)
        {
            return null;
        }

        var tabIndex = firstLine.IndexOf('\t');
        if (tabIndex < 0)
        {
            return null;
        }

        var refPath = firstLine[(tabIndex + 1)..].Trim();
        const string prefix = "refs/tags/";
        return refPath.StartsWith(prefix, StringComparison.Ordinal) ? refPath[prefix.Length..] : refPath;
    }

    public async Task<string> GetLatestCommitShaAsync(string repoUrl, string branch, CancellationToken ct)
    {
        var output = await RunGitAsync(["ls-remote", ApplyAuth(repoUrl), branch], ct);
        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidOperationException($"Branch '{branch}' not found on {repoUrl}.");
        return firstLine.Split('\t')[0].Trim();
    }

    /// <summary>Injects the configured token into github.com HTTPS URLs so private repos work over `git`.</summary>
    private string ApplyAuth(string repoUrl)
    {
        var token = options.CurrentValue.Token;
        if (string.IsNullOrWhiteSpace(token) ||
            !Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return repoUrl;
        }

        return $"https://{token}@{uri.Host}{uri.PathAndQuery}";
    }

    private static async Task<string> RunGitAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            var safeArgs = RedactCredentials(string.Join(' ', args));
            var safeError = RedactCredentials(stdErr);
            throw new InvalidOperationException($"git {safeArgs} failed: {safeError}");
        }

        return stdOut;
    }

    /// <summary>Strips any embedded `https://TOKEN@host` credentials before the string reaches logs/exceptions.</summary>
    private static string RedactCredentials(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"https://[^@/\s]+@", "https://***@");

    private sealed class ClonedRepo(string path) : IClonedRepo
    {
        public string Path { get; } = path;

        public RepoSourceFiles ReadSourceFiles() => new()
        {
            DockerfileContent = ReadIfExists("Dockerfile"),
            ComposeContent = ReadIfExists("docker-compose.yml")
                ?? ReadIfExists("docker-compose.yaml")
                ?? ReadIfExists("compose.yml")
                ?? ReadIfExists("compose.yaml"),
            EnvExampleContent = ReadIfExists(".env.example"),
            ReadmeContent = ReadIfExists("README.md") ?? ReadIfExists("readme.md"),
        };

        private string? ReadIfExists(string relativeFile)
        {
            var full = System.IO.Path.Combine(Path, relativeFile);
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }

        public Task CopyToAsync(string destinationDirectory, CancellationToken ct)
        {
            CopyDirectory(Path, destinationDirectory, ct);
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> FindDockerfileCandidates()
        {
            var results = new List<string>();
            ScanForDockerfiles(Path, results);
            return [.. results.OrderBy(r => r, StringComparer.Ordinal)];
        }

        private void ScanForDockerfiles(string dir, List<string> results)
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (entry.Name is ".git" or "node_modules")
                {
                    continue;
                }

                if (entry is DirectoryInfo subdir)
                {
                    ScanForDockerfiles(subdir.FullName, results);
                    continue;
                }

                if (!DockerfileNameRegex.IsMatch(entry.Name))
                {
                    continue;
                }

                var relative = System.IO.Path.GetRelativePath(Path, entry.FullName).Replace('\\', '/');
                if (!string.Equals(relative, "Dockerfile", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(relative);
                }
            }
        }

        public string? ReadFile(string relativePath)
        {
            var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!System.IO.Path.GetFullPath(full).StartsWith(System.IO.Path.GetFullPath(Path), StringComparison.Ordinal))
            {
                return null;
            }

            return File.Exists(full) ? File.ReadAllText(full) : null;
        }

        private static readonly Regex DockerfileNameRegex =
            new(@"^Dockerfile(\..+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void CopyDirectory(string sourceDir, string destinationDir, CancellationToken ct)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (var entry in new DirectoryInfo(sourceDir).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();

                if (entry.Name == ".git")
                {
                    continue;
                }

                var destPath = System.IO.Path.Combine(destinationDir, entry.Name);
                if (entry is DirectoryInfo dir)
                {
                    CopyDirectory(dir.FullName, destPath, ct);
                }
                else
                {
                    File.Copy(entry.FullName, destPath, overwrite: true);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    // git marks pack files read-only; Directory.Delete throws
                    // UnauthorizedAccessException on those unless we clear the
                    // attribute first.
                    ClearReadOnlyAttributes(Path);
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temp dir; not worth failing the caller over.
            }

            return ValueTask.CompletedTask;
        }

        private static void ClearReadOnlyAttributes(string root)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }
    }
}
