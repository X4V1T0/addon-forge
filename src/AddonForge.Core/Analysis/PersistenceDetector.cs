using System.Text.RegularExpressions;
using AddonForge.Core.Models;

namespace AddonForge.Core.Analysis;

/// <summary>
/// Finds container paths an app writes state to that Home Assistant would
/// silently lose on a rebuild (only /data is kept per addon). Deterministic
/// and heuristic: VOLUME declarations and compose volumes (strong), ENV vars
/// naming a data/state/config directory, and directories created writable at
/// build time (`mkdir` + `chmod`/`chown`). A wrong guess here is harmless -
/// nothing is persisted until the user confirms - but a miss means data loss
/// only noticed after an update, which is what this exists to prevent.
/// </summary>
public static partial class PersistenceDetector
{
    [GeneratedRegex(@"\\\r?\n")]
    private static partial Regex LineContinuation();

    [GeneratedRegex(@"^\s*RUN\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RunLine();

    [GeneratedRegex(@"^\s*WORKDIR\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WorkdirLine();

    [GeneratedRegex(@"^\s*(?:COPY|ADD)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CopyLine();

    [GeneratedRegex(@"^\s*ENV\s+(.+?)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex EnvLine();

    [GeneratedRegex("([A-Za-z_][A-Za-z0-9_]*)=(\"[^\"]*\"|'[^']*'|\\S*)")]
    private static partial Regex KeyValue();

    private static readonly string[] StrongEnvKeywords = ["DATA", "STATE", "STORAGE", "DB", "DATABASE", "PERSIST"];

    private static readonly string[] WeakEnvKeywords = ["CONFIG", "CONF", "UPLOAD", "LIBRARY", "MEDIA", "STORE", "CACHE"];

    private static readonly string[] EnvNameSuffixes = ["DIR", "DIRECTORY", "PATH", "FOLDER", "ROOT", "HOME", "LOCATION"];

    /// <summary>
    /// Paths that must never be symlinked to /data or /share, regardless of
    /// who asks. Deliberately does NOT include /data itself - see
    /// <see cref="IsPersistable"/>'s own <c>allowUnderData</c> parameter for
    /// why that one's forbidden only some of the time.
    /// </summary>
    private static readonly string[] ForbiddenRoots =
    [
        "/share", "/proc", "/sys", "/dev", "/run", "/var/run", "/tmp", "/bin", "/sbin", "/lib", "/lib64",
        // Not "/usr" as a whole subtree: that also vetoed real data
        // directories under it (geodata's `VOLUME /usr/share/geodata`,
        // nginx's /usr/share/nginx/html...), which were then silently
        // dropped. Only what a container actually needs in order to run.
        "/usr/bin", "/usr/sbin", "/usr/lib", "/usr/lib64", "/usr/libexec", "/usr/include",
        "/usr/local/bin", "/usr/local/sbin",
        "/boot", "/etc/localtime", "/etc/timezone", "/etc/hosts", "/etc/resolv.conf", "/etc/passwd",
        "/etc/group", "/etc/shadow", "/etc/ssl", "/etc/ca-certificates", "/addon-forge-entrypoint.sh",
        "/addon-forge-jq", "/addon-forge-config.template", "/addon-forge-seed", "/busybox",
    ];

    /// <summary>
    /// Forbidden only as exactly this path, not as a prefix - replacing the
    /// whole of one of these with a symlink would break the container, but
    /// a data directory somewhere *inside* it is fine (see <see cref="ForbiddenRoots"/>).
    /// </summary>
    private static readonly string[] ForbiddenExactPaths = ["/usr", "/usr/local", "/usr/share"];

    /// <summary>Directories that hold app code - never guessed from an ENV name or a mkdir (a VOLUME/compose entry or the user's own choice still can).</summary>
    private static readonly string[] CodeDirs = ["/app", "/srv", "/opt", "/home", "/root", "/workspace", "/code", "/src"];

    public static List<PersistentPathCandidate> Detect(string? dockerfileContent, IEnumerable<VolumeMapping> composeVolumes)
    {
        var byPath = new Dictionary<string, PersistentPathCandidate>(StringComparer.Ordinal);

        void Add(string? rawPath, string reason, bool recommended)
        {
            var path = Normalize(rawPath);
            if (path is null || !IsPersistable(path))
            {
                return;
            }

            if (byPath.TryGetValue(path, out var existing))
            {
                if (recommended && !existing.Recommended)
                {
                    byPath[path] = existing with { Recommended = true };
                }

                return;
            }

            if (recommended && IsContentLibrary(path))
            {
                recommended = false;
                reason += " - looks like a media/content library you provide, not app state";
            }

            byPath[path] = new PersistentPathCandidate(path, reason, recommended);
        }

        foreach (var v in composeVolumes.Where(v => !v.ReadOnly))
        {
            Add(v.ContainerPath, "compose volume", recommended: true);
        }

        if (!string.IsNullOrWhiteSpace(dockerfileContent))
        {
            var joined = LineContinuation().Replace(dockerfileContent, " ");

            foreach (var v in DockerfileParser.ParseVolumes(joined))
            {
                Add(v.ContainerPath, "Dockerfile VOLUME", recommended: true);
            }

            var codePaths = CollectCodePaths(joined);

            foreach (var (name, value) in EnumerateEnv(joined))
            {
                var kind = ClassifyEnvName(name);
                if (kind is null || IsCodePath(value, codePaths))
                {
                    continue;
                }

                Add(value, $"Dockerfile ENV {name}", recommended: kind == true);
            }

            foreach (var path in EnumerateWritableDirs(joined))
            {
                if (!IsCodePath(path, codePaths))
                {
                    Add(path, "created writable at build time (mkdir + chmod/chown)", recommended: true);
                }
            }
        }

        return [.. byPath.Values.OrderByDescending(c => c.Recommended).ThenBy(c => c.Path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Trims, drops a trailing slash and any duplicates, and discards anything
    /// unsafe or meaningless (relative paths, forbidden roots, file-looking
    /// paths, and any path nested inside another one already in the list).
    /// Used both on detected candidates and on whatever the user typed in.
    /// <paramref name="allowUnderData"/> should be true only when normalizing
    /// *shared* paths (see ManagedAddon.SharedPaths): persisting something
    /// already under /data is meaningless (it already survives a rebuild),
    /// but exposing part of it to a *different* addon via /share is exactly
    /// the point - e.g. a reverse proxy addon's own already-persisted access
    /// log, read by a sibling addon that can't otherwise see inside its
    /// private /data at all.
    /// </summary>
    public static List<string> NormalizePaths(IEnumerable<string> raw, bool allowUnderData = false)
    {
        var valid = raw
            .Select(Normalize)
            .Where(p => p is not null && IsPersistable(p, allowUnderData))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p.Length)
            .ToList();

        var result = new List<string>();
        foreach (var p in valid)
        {
            if (!result.Any(parent => p.StartsWith(parent + "/", StringComparison.Ordinal)))
            {
                result.Add(p);
            }
        }

        return [.. result.OrderBy(p => p, StringComparer.Ordinal)];
    }

    /// <summary>Where a persisted path actually lives under /data.</summary>
    public static string TargetFor(string path) => "/data/persist/" + path.Trim('/').Replace('/', '_');

    /// <summary>
    /// Where a shared path actually lives under /share — namespaced by this
    /// addon's own slug so two addons that each share a path with the same
    /// last segment (e.g. both have a "logs" dir) never collide in the one
    /// /share folder every addon can see. Unlike <see cref="TargetFor"/>,
    /// this isn't about surviving a rebuild (this addon's own /data already
    /// does that) — it's about handing a path to a *different* addon, which
    /// can only ever see this one's files under /share, never under /data.
    /// </summary>
    public static string SharedTargetFor(string slug, string path) =>
        $"/share/{slug}/" + path.Trim('/').Replace('/', '_');

    /// <summary>
    /// A user-supplied library (music, photos, downloads...) is content the
    /// user points the app at - via an addon option or a `map:` folder - not
    /// state the app creates, so an update losing "it" loses nothing. Flagging
    /// it as data at risk (e.g. Navidrome's `VOLUME /music`) is a false alarm.
    /// </summary>
    private static readonly string[] ContentLibraryNames =
    [
        "music", "media", "movies", "tv", "shows", "videos", "video", "photos", "pictures", "images", "library",
        "books", "comics", "audiobooks", "podcasts", "downloads", "audio", "share", "shared", "import", "incoming",
    ];

    private static bool IsContentLibrary(string path) =>
        ContentLibraryNames.Contains(path[(path.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase);

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var path = raw.Trim().Trim('"', '\'');
        if (!path.StartsWith('/') || path.Contains('$') || path.Contains('{') || path.Contains('*') ||
            path.Contains('\n') || path.Contains('\r') || path.Contains('\0'))
        {
            return null;
        }

        while (path.Length > 1 && path.EndsWith('/'))
        {
            path = path[..^1];
        }

        return path.Length < 2 ? null : path;
    }

    private static bool IsPersistable(string path, bool allowUnderData = false)
    {
        if (path.Contains("//") || path.Contains("/../") || path.EndsWith("/.."))
        {
            return false;
        }

        if (!allowUnderData && (path == "/data" || path.StartsWith("/data/", StringComparison.Ordinal)))
        {
            return false;
        }

        if (ForbiddenExactPaths.Contains(path))
        {
            return false;
        }

        foreach (var forbidden in ForbiddenRoots)
        {
            if (path == forbidden || path.StartsWith(forbidden + "/", StringComparison.Ordinal))
            {
                return false;
            }
        }

        // A file (e.g. a compose bind-mounted config.yml or a socket) can't
        // become a directory symlink without breaking whatever reads it.
        var last = path[(path.LastIndexOf('/') + 1)..];
        return !Regex.IsMatch(last, @"\.[A-Za-z0-9]{1,5}$");
    }

    /// <summary>true = strong (pre-select), false = weak (list only), null = not a data-path variable at all.</summary>
    private static bool? ClassifyEnvName(string name)
    {
        var upper = name.ToUpperInvariant();
        var parts = upper.Split('_');

        if (!EnvNameSuffixes.Contains(parts[^1]))
        {
            return null;
        }

        if (StrongEnvKeywords.Any(k => parts.Contains(k)))
        {
            return true;
        }

        return WeakEnvKeywords.Any(k => parts.Contains(k)) ? false : null;
    }

    private static IEnumerable<(string Name, string Value)> EnumerateEnv(string dockerfile)
    {
        foreach (Match line in EnvLine().Matches(dockerfile))
        {
            var raw = line.Groups[1].Value.Trim();
            if (raw.Contains('='))
            {
                foreach (Match kv in KeyValue().Matches(raw))
                {
                    yield return (kv.Groups[1].Value, kv.Groups[2].Value.Trim('"', '\''));
                }
            }
            else
            {
                var space = raw.IndexOf(' ');
                if (space > 0)
                {
                    yield return (raw[..space], raw[(space + 1)..].Trim().Trim('"', '\''));
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateWritableDirs(string dockerfile)
    {
        foreach (Match run in RunLine().Matches(dockerfile))
        {
            var segments = Regex.Split(run.Groups[1].Value, @"&&|;|\|\|");
            var mkdirPaths = new List<string>();
            var permissive = new List<string>();

            foreach (var segment in segments.Select(s => s.Trim()))
            {
                var tokens = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                {
                    continue;
                }

                if (tokens[0] == "mkdir")
                {
                    mkdirPaths.AddRange(tokens.Skip(1).Where(t => t.StartsWith('/')));
                }
                else if (tokens[0] is "chmod" or "chown")
                {
                    permissive.AddRange(tokens.Skip(1));
                }
            }

            foreach (var path in mkdirPaths)
            {
                var normalized = Normalize(path);
                if (normalized is not null && permissive.Any(t => Normalize(t) == normalized))
                {
                    yield return path;
                }
            }
        }
    }

    private static List<string> CollectCodePaths(string dockerfile)
    {
        var result = new List<string>();

        foreach (Match m in WorkdirLine().Matches(dockerfile))
        {
            if (Normalize(m.Groups[1].Value) is { } p)
            {
                result.Add(p);
            }
        }

        foreach (Match m in CopyLine().Matches(dockerfile))
        {
            var last = m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (Normalize(last) is { } p)
            {
                result.Add(p);
            }
        }

        return result;
    }

    /// <summary>True for a path that IS, or contains, the app's own code (WORKDIR / a COPY destination): persisting it would replace the app itself.</summary>
    private static bool IsCodePath(string? rawPath, List<string> codePaths)
    {
        var path = Normalize(rawPath);
        if (path is null)
        {
            return true;
        }

        if (CodeDirs.Contains(path))
        {
            return true;
        }

        return codePaths.Any(c => c == path || c.StartsWith(path + "/", StringComparison.Ordinal));
    }
}
