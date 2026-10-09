using System.Text.Json;
using AddonForge.Core.Analysis;
using AddonForge.Core.Models;

namespace AddonForge.Core.Generation;

/// <summary>
/// The SourceKind.DockerImage equivalent of RepoAnalyzer + a Dockerfile: turns
/// what IContainerRegistryClient.InspectAsync read out of a published image's
/// own manifest/config into the same RepoAnalysisResult shape a git-sourced
/// Dockerfile analysis produces, plus (when AddonConfigGenerator decides
/// wrapping is needed - see its own doc comment) a minimal synthetic
/// Dockerfile wrapped around the image so the rest of the generation pipeline
/// (EntrypointWrapperGenerator, distroless detection, VOLUME stripping...)
/// runs completely unchanged, exactly as if this had been the source repo's
/// own Dockerfile all along.
/// </summary>
public static class ImageSourceGenerator
{
    /// <summary>
    /// Env vars nearly every base image bakes in that are toolchain/runtime
    /// plumbing, never something a user configures - offering these as addon
    /// options would just be noise. An image's own resolved Env includes
    /// everything set by every base layer (unlike a single Dockerfile's own
    /// ENV lines, which DockerfileParser.IsUserConfigurable's narrower list
    /// already handles), so this list is deliberately broader. Best-effort,
    /// same spirit as PersistenceDetector's own heuristics: a name missed
    /// here still just becomes a normal, reviewable option - nothing silently
    /// breaks, it's only ever noise reduction.
    /// </summary>
    private static readonly HashSet<string> NoiseEnvNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "LANG", "LANGUAGE", "LC_ALL", "TERM", "HOSTNAME", "PWD", "SHLVL",
        "DEBIAN_FRONTEND", "container", "TZDATA",
        "GPG_KEY", "GPG_KEYS",
        "PYTHON_VERSION", "PYTHON_PIP_VERSION", "PYTHON_SETUPTOOLS_VERSION", "PYTHON_GET_PIP_URL",
        "PYTHON_GET_PIP_SHA256", "PYTHONDONTWRITEBYTECODE", "PYTHONUNBUFFERED", "PYTHONPATH",
        "GOLANG_VERSION", "GOPATH", "GOTOOLCHAIN", "GOROOT", "GOFLAGS", "GO111MODULE", "CGO_ENABLED",
        "NODE_VERSION", "YARN_VERSION", "NPM_CONFIG_LOGLEVEL", "NPM_CONFIG_UPDATE_NOTIFIER",
        "JAVA_HOME", "JAVA_VERSION", "JAVA_ALPINE_VERSION", "LD_LIBRARY_PATH",
        "CARGO_HOME", "RUSTUP_HOME", "RUST_VERSION",
        "LUA_VERSION", "LUAROCKS_VERSION",
        "GEM_HOME", "BUNDLE_PATH", "BUNDLE_SILENCE_ROOT_WARNING", "BUNDLE_APP_CONFIG",
        "PHP_VERSION", "PHP_INI_DIR", "PHP_CFLAGS", "PHP_CPPFLAGS", "PHP_LDFLAGS", "PHP_MD5", "PHP_ASC_URL",
        "APACHE_CONFDIR", "APACHE_ENVVARS", "APACHE_RUN_USER", "APACHE_RUN_GROUP",
        "NGINX_VERSION", "NJS_VERSION", "PKG_RELEASE", "DPKG_ARCH",
        "MYSQL_MAJOR", "MYSQL_VERSION", "POSTGRES_VERSION", "PG_MAJOR", "PG_VERSION",
        "OPENSSL_VERSION", "S6_VERSION", "S6_OVERLAY_VERSION", "TINI_VERSION", "GOSU_VERSION",
    };

    public static RepoAnalysisResult BuildRepoAnalysisResult(ImageInspectionResult inspection)
    {
        var envVars = inspection.RawEnv
            .Select(ParseEnvEntry)
            .Where(kv => kv is not null && DockerfileParser.IsUserConfigurable(kv.Value.Name) &&
                         !NoiseEnvNames.Contains(kv.Value.Name))
            .Select(kv => DockerfileParser.BuildCandidate(kv!.Value.Name, kv.Value.Value, "image config"))
            .OrderBy(v => v.Name, StringComparer.Ordinal)
            .ToList();

        return new RepoAnalysisResult
        {
            HasDockerfile = true, // a synthetic one always exists once generated - see AddonConfigGenerator.
            HasCompose = false,
            Ports = inspection.Ports,
            Volumes = inspection.Volumes,
            EnvVars = envVars,
            PersistentPathCandidates = PersistenceDetector.Detect(null, inspection.Volumes),
            ReadmeExcerpt = null,
        };
    }

    /// <summary>
    /// A minimal Dockerfile with nothing to build - just the FROM the image
    /// was inspected at, plus its own VOLUME/ENTRYPOINT/CMD re-declared
    /// verbatim (read directly from the image's own config, not guessed) so
    /// PersistenceDetector/EntrypointWrapperGenerator/AddonConfigGenerator see
    /// exactly what they'd see from a real Dockerfile, without needing to
    /// know this image was never actually built from source here at all.
    /// </summary>
    public static string BuildSyntheticDockerfile(ImageReference fullReference, ImageInspectionResult inspection)
    {
        var lines = new List<string>
        {
            $"FROM {fullReference}",
            "# addon-forge: this Dockerfile builds nothing - it only re-declares the",
            "# published image's own VOLUME/ENTRYPOINT/CMD (read from its manifest, not",
            "# guessed) so the rest of addon-forge's generation can treat it exactly",
            "# like a source repo's own Dockerfile.",
        };

        if (inspection.Volumes.Count > 0)
        {
            var paths = inspection.Volumes.Select(v => v.ContainerPath).ToArray();
            lines.Add($"VOLUME {JsonSerializer.Serialize(paths)}");
        }

        if (inspection.Entrypoint is { Count: > 0 })
        {
            lines.Add($"ENTRYPOINT {JsonSerializer.Serialize(inspection.Entrypoint)}");
        }

        if (inspection.Cmd is { Count: > 0 })
        {
            lines.Add($"CMD {JsonSerializer.Serialize(inspection.Cmd)}");
        }

        return string.Join('\n', lines) + '\n';
    }

    private static (string Name, string Value)? ParseEnvEntry(string raw)
    {
        var eq = raw.IndexOf('=');
        return eq <= 0 ? null : (raw[..eq], raw[(eq + 1)..]);
    }
}
