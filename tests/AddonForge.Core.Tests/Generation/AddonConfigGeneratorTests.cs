using AddonForge.Core.Analysis;
using AddonForge.Core.Generation;
using AddonForge.Core.Models;
using Xunit;
using YamlDotNet.Serialization;

namespace AddonForge.Core.Tests.Generation;

public class AddonConfigGeneratorTests
{
    private const string SampleCompose = """
        services:
          app:
            build: .
            ports:
              - "8080:80"
            volumes:
              - ./data:/data
            environment:
              - PUID=1000
              - TZ=Etc/UTC
              - ADMIN_PASSWORD
        """;

    private const string SampleDockerfile = """
        FROM node:20-alpine
        EXPOSE 80
        CMD ["node", "server.js"]
        """;

    private static GeneratedAddonFiles GenerateFromSample(bool ingress = false)
    {
        var sourceFiles = new RepoSourceFiles
        {
            DockerfileContent = SampleDockerfile,
            ComposeContent = SampleCompose,
            ReadmeContent = "# My App\n\nA nice self-hosted app.",
        };
        var analysis = RepoAnalyzer.Analyze(sourceFiles);

        var request = new AddonGenerationRequest
        {
            Slug = "my_app",
            Name = "My App",
            Description = "A nice self-hosted app.",
            SourceRepoUrl = "https://github.com/example/my-app",
            SourceWebUrl = "https://github.com/example/my-app",
            Version = "1.2.3",
            Ref = "v1.2.3",
            Ingress = ingress,
            IngressPort = ingress ? 8080 : null,
            DockerfileContent = sourceFiles.DockerfileContent,
        };

        return AddonConfigGenerator.Generate(analysis, request);
    }

    [Fact]
    public void Generate_ProducesAllExpectedFiles()
    {
        var files = GenerateFromSample();

        Assert.True(files.Files.ContainsKey("config.yaml"));
        Assert.True(files.Files.ContainsKey("DOCS.md"));
        Assert.True(files.Files.ContainsKey("CHANGELOG.md"));
        Assert.True(files.Files.ContainsKey("Dockerfile"));
        // The sample has options, so the original Dockerfile gets the
        // entrypoint-wrapper appendix — see Generate_WithOptions_WrapsEntrypoint...
        // for that behavior specifically; here just confirm the source content survives.
        Assert.StartsWith(SampleDockerfile, files.Files["Dockerfile"]);
    }

    [Fact]
    public void Generate_ConfigYaml_IsValidYamlWithExpectedManifestFields()
    {
        var files = GenerateFromSample();

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);

        Assert.Equal("My App", manifest["name"]);
        Assert.Equal("1.2.3", manifest["version"]);
        Assert.Equal("my_app", manifest["slug"]);
        Assert.Equal("https://github.com/example/my-app", manifest["url"]);
    }

    // Regression coverage for a real bug found against a live Supervisor: a
    // bare image reference (e.g. "example/proxy") was written straight to
    // config.yaml's `url:`, which the Supervisor's own schema validates as an
    // actual URL - it refused to even load the generated config.yaml
    // ("expected a URL for dictionary value"). SourceRepoUrl (the bare
    // reference) and SourceWebUrl (a real link or null) are now separate.
    [Fact]
    public void Generate_NoSourceWebUrl_OmitsUrlFieldRatherThanWritingInvalidOne()
    {
        var sourceFiles = new RepoSourceFiles { DockerfileContent = SampleDockerfile };
        var analysis = RepoAnalyzer.Analyze(sourceFiles);
        var request = new AddonGenerationRequest
        {
            Slug = "proxy", Name = "Proxy", Description = "desc",
            SourceRepoUrl = "example/proxy", // not a URL - SourceWebUrl deliberately left null
            Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = SampleDockerfile,
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        Assert.False(manifest.ContainsKey("url"));
        Assert.DoesNotContain("[example/proxy](example/proxy)", files.Files["DOCS.md"]);
        Assert.Contains("`example/proxy`", files.Files["DOCS.md"]);
    }

    [Fact]
    public void Generate_ConfigYaml_IncludesPortsFromComposeAndDockerfile()
    {
        var files = GenerateFromSample();

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);

        var ports = Assert.IsType<Dictionary<object, object>>(manifest["ports"]);
        Assert.True(ports.ContainsKey("80/tcp"));
    }

    [Fact]
    public void Generate_StringDefaultLikeTrue_StaysAStringInConfigYaml()
    {
        // "BYPASS" used to match Contains("PASS") -> password option whose
        // `true` default was written unquoted, so HA read a bool and
        // rejected the options block ("expected str. Got ...").
        const string dockerfile = "FROM alpine\nENV AGENT_BYPASS_DB_VOLUME_CHECK=true\nENV API_TOKEN=true\nEXPOSE 80\n";
        var sourceFiles = new RepoSourceFiles { DockerfileContent = dockerfile };
        var request = new AddonGenerationRequest
        {
            Slug = "cs", Name = "cs", Description = "d", SourceRepoUrl = "https://x/y", SourceWebUrl = "https://x/y",
            Version = "1.0.0", Ref = "v1.0.0", DockerfileContent = dockerfile,
        };

        var files = AddonConfigGenerator.Generate(RepoAnalyzer.Analyze(sourceFiles), request);

        var manifest = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        var schema = Assert.IsType<Dictionary<object, object>>(manifest["schema"]);
        Assert.Equal("bool", schema["AGENT_BYPASS_DB_VOLUME_CHECK"]);
        Assert.Matches("API_TOKEN: ['\"]true['\"]", files.Files["config.yaml"]);
    }

    [Fact]
    public void Generate_BypassNamedBoolEnv_IsNotTreatedAsPassword()
    {
        // "BYPASS" used to match Contains("PASS") -> password option whose
        // `true` default was written unquoted, so HA read a bool and
        // rejected the options block ("expected str. Got ...").
        const string dockerfile = "FROM alpine\nENV AGENT_BYPASS_DB_VOLUME_CHECK=true\nENV USE_WAL=true\nEXPOSE 80\n";
        var sourceFiles = new RepoSourceFiles { DockerfileContent = dockerfile };
        var request = new AddonGenerationRequest
        {
            Slug = "cs", Name = "cs", Description = "d", SourceRepoUrl = "https://x/y", SourceWebUrl = "https://x/y",
            Version = "1.0.0", Ref = "v1.0.0", DockerfileContent = dockerfile,
        };

        var files = AddonConfigGenerator.Generate(RepoAnalyzer.Analyze(sourceFiles), request);

        var manifest = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        var options = Assert.IsType<Dictionary<object, object>>(manifest["options"]);
        var schema = Assert.IsType<Dictionary<object, object>>(manifest["schema"]);
        Assert.Equal("bool", schema["AGENT_BYPASS_DB_VOLUME_CHECK"]);
        Assert.Equal("true", options["USE_WAL"].ToString()!.ToLowerInvariant());
        Assert.DoesNotContain("password", schema["AGENT_BYPASS_DB_VOLUME_CHECK"].ToString());
    }

    [Fact]
    public void Generate_ConfigYaml_IncludesOptionsAndSchemaForEnvVars()
    {
        var files = GenerateFromSample();

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);

        var options = Assert.IsType<Dictionary<object, object>>(manifest["options"]);
        var schema = Assert.IsType<Dictionary<object, object>>(manifest["schema"]);

        Assert.Equal(1000, Convert.ToInt32(options["PUID"]));
        Assert.Equal("int", schema["PUID"]);

        Assert.Equal("Etc/UTC", options["TZ"]);
        Assert.Equal("str", schema["TZ"]);

        Assert.Equal("password?", schema["ADMIN_PASSWORD"]);
    }

    [Fact]
    public void Generate_WithIngress_OmitsPortsAndSetsIngressPort()
    {
        var files = GenerateFromSample(ingress: true);

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);

        Assert.True(Convert.ToBoolean(manifest["ingress"]));
        Assert.Equal(8080, Convert.ToInt32(manifest["ingress_port"]));
        Assert.False(manifest.ContainsKey("ports"));
    }

    // Regression coverage for a real bug: config.yaml's `environment:` key
    // looks like it should support `{{ options.KEY }}` templating, but
    // confirmed against Home Assistant's own docs, it only ever sets STATIC
    // values - the Supervisor passes the literal, unrendered string through
    // to the container. Home Assistant options can *only* reach the
    // container by wrapping its entrypoint to read /data/options.json itself.
    [Fact]
    public void Generate_WithOptions_WrapsEntrypointInsteadOfUsingBrokenEnvironmentTemplating()
    {
        var files = GenerateFromSample();

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        Assert.False(manifest.ContainsKey("environment"));

        Assert.True(files.Files.ContainsKey(EntrypointWrapperGenerator.ScriptFileName));
        var script = files.Files[EntrypointWrapperGenerator.ScriptFileName];
        Assert.Contains("/data/options.json", script);
        // The sample Dockerfile has no ENTRYPOINT, only `CMD ["node", "server.js"]` -
        // that's what the wrapper must still hand off to once it's exported the options.
        Assert.Contains("exec 'node' 'server.js'", script);

        var dockerfile = files.Files["Dockerfile"];
        Assert.Contains("ENTRYPOINT [\"/addon-forge-entrypoint.sh\"]", dockerfile);
        Assert.StartsWith(SampleDockerfile, dockerfile); // original content preserved, only appended to
    }

    [Fact]
    public void Generate_NoOptions_DoesNotWrapEntrypointAtAll()
    {
        var sourceFiles = new RepoSourceFiles { DockerfileContent = SampleDockerfile };
        var analysis = RepoAnalyzer.Analyze(sourceFiles);
        var request = new AddonGenerationRequest
        {
            Slug = "my_app", Name = "My App", Description = "desc",
            SourceRepoUrl = "https://github.com/example/my-app", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = SampleDockerfile,
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        Assert.False(files.Files.ContainsKey(EntrypointWrapperGenerator.ScriptFileName));
        Assert.Equal(SampleDockerfile, files.Files["Dockerfile"]);
    }

    [Fact]
    public void Generate_WithMapEntries_WritesThemToConfigYaml()
    {
        // addon-forge can't reliably guess which host folder a container path
        // like /music is supposed to be, so a user-specified `map:` (e.g. for
        // Home Assistant's shared /media folder) is passed straight through.
        var sourceFiles = new RepoSourceFiles { DockerfileContent = SampleDockerfile };
        var analysis = RepoAnalyzer.Analyze(sourceFiles);
        var request = new AddonGenerationRequest
        {
            Slug = "my_app", Name = "My App", Description = "desc",
            SourceRepoUrl = "https://github.com/example/my-app", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = SampleDockerfile,
            MapEntries = ["media:rw", "share:ro"],
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        var map = Assert.IsType<List<object>>(manifest["map"]);
        Assert.Contains("media:rw", map);
        Assert.Contains("share:ro", map);
    }

    [Fact]
    public void Generate_WithConfigFileTemplate_WrapsEntrypointAndWritesTemplate()
    {
        // A config-file-only app (no detected env var options at all) still
        // needs the entrypoint wrapped - that's what actually renders and
        // writes the file before the app starts.
        var sourceFiles = new RepoSourceFiles { DockerfileContent = SampleDockerfile };
        var analysis = RepoAnalyzer.Analyze(sourceFiles);
        var request = new AddonGenerationRequest
        {
            Slug = "my_app", Name = "My App", Description = "desc",
            SourceRepoUrl = "https://github.com/example/my-app", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = SampleDockerfile,
            ConfigFileTemplate = "weather:\n  location: ${WEATHER_LOCATION}\n",
            ConfigFileDestPath = "/config/config.yaml",
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        Assert.True(files.Files.ContainsKey(EntrypointWrapperGenerator.ScriptFileName));
        Assert.Equal(
            "weather:\n  location: ${WEATHER_LOCATION}\n",
            files.Files[EntrypointWrapperGenerator.ConfigTemplateFileName]);

        var script = files.Files[EntrypointWrapperGenerator.ScriptFileName];
        Assert.Contains("/config/config.yaml", script);
        Assert.Contains("/addon-forge-jq", script);
        Assert.DoesNotContain("envsubst", script!);

        var dockerfile = files.Files["Dockerfile"];
        Assert.Contains($"COPY {EntrypointWrapperGenerator.ConfigTemplateFileName}", dockerfile);
        // setuid-root, not a package install - see
        // EntrypointWrapperGeneratorTests for why nothing here needs one.
        Assert.Contains("COPY --from=ghcr.io/jqlang/jq:latest --chmod=4755 /jq /addon-forge-jq", dockerfile);
        Assert.DoesNotContain("gettext", dockerfile!);

        Assert.Contains("/config/config.yaml", files.Files["DOCS.md"]);

        // The pre-seed mechanism: the destination gets COPYed in world-writable
        // (666) so a non-root image's own runtime write can succeed, and its
        // directory is force-created with the execute bit (777) via a
        // dedicated seed file - see EntrypointWrapperGeneratorTests for the
        // full story of why jq's setuid bit alone can't cover this.
        Assert.True(files.Files.ContainsKey(EntrypointWrapperGenerator.SeedFileName));
        Assert.Equal("", files.Files[EntrypointWrapperGenerator.SeedFileName]);
        Assert.Contains("COPY --chmod=777 addon-forge-seed /config/.addon-forge-keep", dockerfile);
        Assert.Contains("COPY --chmod=666 addon-forge-config.template /config/config.yaml", dockerfile);
    }

    // Regression coverage for a real build failure: a distroless final stage
    // (e.g. example-app's gcr.io/distroless/static-debian13:nonroot) has no
    // shell, so wrapping its entrypoint appended a RUN line that needs
    // /bin/sh - which doesn't exist there - and the Supervisor's build
    // failed on it. Distroless must be treated the same as `FROM scratch`.
    [Fact]
    public void Generate_DistrolessFinalStageWithOptions_SkipsWrappingAndWarnsInDocs()
    {
        const string distrolessDockerfile =
            "FROM golang:1 AS build\nRUN go build -o /app .\n" +
            "FROM gcr.io/distroless/static-debian13:nonroot\nCOPY --from=build /app /app\nENTRYPOINT [\"/app\"]\n";
        var sourceFiles = new RepoSourceFiles { DockerfileContent = distrolessDockerfile, ComposeContent = SampleCompose };
        var analysis = RepoAnalyzer.Analyze(sourceFiles);
        var request = new AddonGenerationRequest
        {
            Slug = "my_app", Name = "My App", Description = "desc",
            SourceRepoUrl = "https://github.com/example/my-app", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = distrolessDockerfile,
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        Assert.False(files.Files.ContainsKey(EntrypointWrapperGenerator.ScriptFileName));
        Assert.Equal(distrolessDockerfile, files.Files["Dockerfile"]);
        Assert.Contains("distroless", files.Files["DOCS.md"]);
    }

    [Fact]
    public void Generate_DistrolessWithAllowDistrolessDebugShell_WrapsEntrypointAndNotesInDocs()
    {
        const string distrolessDockerfile =
            "FROM golang:1 AS build\nRUN go build -o /app .\n" +
            "FROM gcr.io/distroless/static-debian13:nonroot\nCOPY --from=build /app /app\nENTRYPOINT [\"/app\"]\n";
        var sourceFiles = new RepoSourceFiles { DockerfileContent = distrolessDockerfile, ComposeContent = SampleCompose };
        var analysis = RepoAnalyzer.Analyze(sourceFiles);
        var request = new AddonGenerationRequest
        {
            Slug = "my_app", Name = "My App", Description = "desc",
            SourceRepoUrl = "https://github.com/example/my-app", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = distrolessDockerfile,
            AllowDistrolessDebugShell = true,
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        Assert.True(files.Files.ContainsKey(EntrypointWrapperGenerator.ScriptFileName));
        Assert.Contains("gcr.io/distroless/static-debian13:debug-nonroot", files.Files["Dockerfile"]);
        Assert.Contains("debug", files.Files["DOCS.md"]);

        // The actual fix: nothing here needs a package manager (or root, or
        // a privilege drop back afterwards) at all - jq is fetched from its
        // own official image and made setuid-root instead (see
        // EntrypointWrapperGeneratorTests for the full story of why).
        var dockerfile = files.Files["Dockerfile"];
        Assert.Contains("COPY --from=ghcr.io/jqlang/jq:latest --chmod=4755 /jq /addon-forge-jq", dockerfile);
        Assert.Contains("COPY --chmod=755 addon-forge-entrypoint.sh", dockerfile);
        var appendix = dockerfile[dockerfile.IndexOf("addon-forge:", StringComparison.Ordinal)..];
        Assert.DoesNotContain("RUN", appendix);
        Assert.DoesNotContain("USER", appendix);

        var script = files.Files[EntrypointWrapperGenerator.ScriptFileName];
        Assert.StartsWith("#!/busybox/sh\n", script);
        Assert.Contains("/addon-forge-jq", script);
    }

    [Fact]
    public void Generate_NoConfigFileTemplate_DoesNotWriteTemplateFile()
    {
        var files = GenerateFromSample();

        Assert.False(files.Files.ContainsKey(EntrypointWrapperGenerator.ConfigTemplateFileName));
    }

    [Fact]
    public void Generate_DocsMd_ListsDetectedOptions()
    {
        var files = GenerateFromSample();

        Assert.Contains("PUID", files.Files["DOCS.md"]);
        Assert.Contains("v1.2.3", files.Files["DOCS.md"]);
    }

    private const string PersistDockerfile =
        "FROM node:22-slim\nWORKDIR /app\nENV STATE_DIR=/var/sub-wave\nVOLUME /var/sub-wave\n" +
        "RUN mkdir -p /var/sub-wave && chmod 777 /var/sub-wave\nENTRYPOINT [\"/usr/local/bin/supervisor\"]\n";

    private static GeneratedAddonFiles GenerateForPersistence(string dockerfile, params string[] persistentPaths)
    {
        var analysis = RepoAnalyzer.Analyze(new RepoSourceFiles { DockerfileContent = dockerfile });
        var request = new AddonGenerationRequest
        {
            Slug = "my_app", Name = "My App", Description = "desc",
            SourceRepoUrl = "https://github.com/example/my-app", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = dockerfile,
            PersistentPaths = persistentPaths,
        };

        return AddonConfigGenerator.Generate(analysis, request);
    }

    [Fact]
    public void Generate_WithPersistentPaths_WrapsEntrypointStripsVolumeAndDocumentsIt()
    {
        var files = GenerateForPersistence(PersistDockerfile, "/var/sub-wave");

        var script = files.Files[EntrypointWrapperGenerator.ScriptFileName];
        Assert.Contains("addon_forge_persist '/var/sub-wave' '/data/persist/var_sub-wave'", script);

        var dockerfile = files.Files["Dockerfile"];
        Assert.DoesNotContain("\nVOLUME /var/sub-wave", dockerfile);
        Assert.Contains("removed (symlinked elsewhere instead)", dockerfile);
        Assert.Contains("/data/persist/var_sub-wave", files.Files["DOCS.md"]);
    }

    [Fact]
    public void Generate_DetectedButNotPersisted_WarnsInDocs()
    {
        var files = GenerateForPersistence(PersistDockerfile);

        Assert.Contains("Not persisted", files.Files["DOCS.md"]);
        Assert.Contains("/var/sub-wave", files.Files["DOCS.md"]);
        Assert.Contains("\nVOLUME /var/sub-wave", files.Files["Dockerfile"]);
    }

    [Fact]
    public void Generate_PersistentPathsOnShelllessImage_WarnsInsteadOfClaimingSuccess()
    {
        var files = GenerateForPersistence(
            "FROM golang:1 AS b\nRUN go build -o /app .\nFROM scratch\nCOPY --from=b /app /app\nENTRYPOINT [\"/app\"]\n",
            "/var/lib/app");

        Assert.False(files.Files.ContainsKey(EntrypointWrapperGenerator.ScriptFileName));
        Assert.Contains("not** persisted", files.Files["DOCS.md"]);
    }

    private const string SharedPathDockerfile =
        "FROM alpine:3\nVOLUME /data/nginx/logs\nENTRYPOINT [\"/usr/local/bin/entrypoint.sh\"]\n";

    [Fact]
    public void Generate_WithSharedPaths_WrapsEntrypointAddsShareRwMapStripsVolumeAndDocumentsIt()
    {
        var analysis = RepoAnalyzer.Analyze(new RepoSourceFiles { DockerfileContent = SharedPathDockerfile });
        var request = new AddonGenerationRequest
        {
            Slug = "proxy", Name = "Proxy", Description = "desc",
            SourceRepoUrl = "https://github.com/example/proxy", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = SharedPathDockerfile,
            SharedPaths = ["/data/nginx/logs"],
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        var script = files.Files[EntrypointWrapperGenerator.ScriptFileName];
        Assert.Contains("addon_forge_persist '/data/nginx/logs' '/share/proxy/data_nginx_logs'", script);

        var dockerfile = files.Files["Dockerfile"];
        Assert.DoesNotContain("\nVOLUME /data/nginx/logs", dockerfile);
        Assert.Contains("removed (symlinked elsewhere instead)", dockerfile);

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        var map = Assert.IsType<List<object>>(manifest["map"]);
        Assert.Contains("share:rw", map);

        Assert.Contains("/share/proxy/data_nginx_logs", files.Files["DOCS.md"]);
        Assert.Contains("Shared with other addons", files.Files["DOCS.md"]);
    }

    // Regression coverage for a real bug: geodata's own data directory,
    // `VOLUME /usr/share/geodata`, sits under /usr, which used to be forbidden
    // as a whole subtree - the shared path was dropped silently and the
    // addon came out with no symlink, no `share:rw` and the VOLUME intact.
    [Fact]
    public void Generate_SharedPathUnderUsr_IsSymlinkedIntoShare()
    {
        const string dockerfile =
            "FROM example/geodata:v8.0.0\nVOLUME [\"/usr/share/geodata\"]\nENTRYPOINT [\"/usr/bin/entry.sh\"]\n";
        var analysis = RepoAnalyzer.Analyze(new RepoSourceFiles { DockerfileContent = dockerfile });
        var request = new AddonGenerationRequest
        {
            Slug = "geodata", Name = "geodata", Description = "desc",
            SourceRepoUrl = "example/geodata", Version = "8.0.0", Ref = "v8.0.0",
            DockerfileContent = dockerfile,
            SharedPaths = ["/usr/share/geodata"],
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        Assert.Contains(
            "addon_forge_persist '/usr/share/geodata' '/share/geodata/usr_share_geodata'",
            files.Files[EntrypointWrapperGenerator.ScriptFileName]);
        Assert.DoesNotContain("\nVOLUME [\"/usr/share/geodata\"]", files.Files["Dockerfile"]);

        var manifest = new DeserializerBuilder().Build()
            .Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        Assert.Contains("share:rw", Assert.IsType<List<object>>(manifest["map"]));
    }

    private static (RepoAnalysisResult Analysis, AddonGenerationRequest Request) VolumeImageRequest(
        params string[] sharedPaths)
    {
        var reference = ImageReference.Parse("example/geodata:v8.0.0");
        var inspection = new ImageInspectionResult
        {
            Digest = "sha256:abc",
            Volumes = [new VolumeMapping { ContainerPath = "/usr/share/geodata" }],
            Entrypoint = ["/usr/bin/entry.sh"],
        };
        var request = new AddonGenerationRequest
        {
            Slug = "geodata", Name = "geodata", Description = "desc",
            SourceRepoUrl = "example/geodata", Version = "8.0.0", Ref = "v8.0.0",
            DockerfileContent = ImageSourceGenerator.BuildSyntheticDockerfile(reference, inspection),
            Image = reference.ToString(),
            SharedPaths = sharedPaths,
        };

        return (ImageSourceGenerator.BuildRepoAnalysisResult(inspection), request);
    }

    // Regression coverage for a real failure: example/geodata declares
    // `VOLUME /usr/share/geodata`, and a VOLUME is inherited from the base image
    // - a Dockerfile on top can't remove it (confirmed directly). Docker
    // mounts a volume there, `rm -rf` on it fails with "Resource busy", and
    // the symlink was never made - with only one line in the addon's log to
    // say so. Fail at generation instead, where it can be explained.
    [Fact]
    public void Generate_ImageSharedPathIsAnInheritedVolume_ThrowsWithExplanation()
    {
        var (analysis, request) = VolumeImageRequest("/usr/share/geodata");

        var ex = Assert.Throws<InvalidOperationException>(() => AddonConfigGenerator.Generate(analysis, request));

        Assert.Contains("/usr/share/geodata", ex.Message);
        Assert.Contains("VOLUME", ex.Message);
    }

    // An ancestor of an inherited volume would have to remove the mount point
    // to become a symlink, so it's blocked too; a path *inside* the volume
    // isn't the mount point itself, so it isn't.
    [Fact]
    public void Generate_ImagePersistentPathAncestorOfInheritedVolume_ThrowsButInsideItDoesNot()
    {
        var reference = ImageReference.Parse("example/app:1.0");
        var inspection = new ImageInspectionResult
        {
            Digest = "sha256:abc",
            Volumes = [new VolumeMapping { ContainerPath = "/var/lib/app/data" }],
            Entrypoint = ["/app"],
        };
        var analysis = ImageSourceGenerator.BuildRepoAnalysisResult(inspection);
        AddonGenerationRequest RequestFor(string persistentPath) => new()
        {
            Slug = "app", Name = "app", Description = "desc", SourceRepoUrl = "example/app",
            Version = "1.0", Ref = "1.0", Image = reference.ToString(),
            DockerfileContent = ImageSourceGenerator.BuildSyntheticDockerfile(reference, inspection),
            PersistentPaths = [persistentPath],
        };

        Assert.Throws<InvalidOperationException>(
            () => AddonConfigGenerator.Generate(analysis, RequestFor("/var/lib/app")));

        AddonConfigGenerator.Generate(analysis, RequestFor("/var/lib/app/data/sub"));
        AddonConfigGenerator.Generate(analysis, RequestFor("/var/lib/application"));
    }

    // A path that isn't one of the image's own volumes is unaffected - this
    // is what to use instead (point the app at it, e.g. geodata's
    // GEODATA_DIR).
    [Fact]
    public void Generate_ImageSharedPathNotAVolume_IsSymlinkedAsUsual()
    {
        var (analysis, request) = VolumeImageRequest("/geodata");

        var files = AddonConfigGenerator.Generate(analysis, request);

        Assert.Contains(
            "addon_forge_persist '/geodata' '/share/geodata/geodata'",
            files.Files[EntrypointWrapperGenerator.ScriptFileName]);
    }

    // A git-sourced addon's own Dockerfile VOLUME lines ARE stripped, so the
    // same path is fine there - only a *published image's* inherited volumes
    // are the problem.
    [Fact]
    public void Generate_GitSourcedSharedPathThatIsItsOwnVolume_StillWorks()
    {
        var (analysis, imageRequest) = VolumeImageRequest("/usr/share/geodata");
        var gitRequest = new AddonGenerationRequest
        {
            Slug = "geodata", Name = "geodata", Description = "desc",
            SourceRepoUrl = "https://github.com/example/geodata", Version = "1", Ref = "v1",
            DockerfileContent = imageRequest.DockerfileContent,
            SharedPaths = ["/usr/share/geodata"],
            // Image deliberately unset: this is a git-sourced build.
        };

        var files = AddonConfigGenerator.Generate(analysis, gitRequest);

        Assert.Contains("addon_forge_persist '/usr/share/geodata'", files.Files[EntrypointWrapperGenerator.ScriptFileName]);
        Assert.DoesNotContain("\nVOLUME [\"/usr/share/geodata\"]", files.Files["Dockerfile"]);
    }

    // A user-supplied `share:ro` (added for some other reason, or left over
    // from before this addon needed to write shared paths) isn't enough -
    // the addon needs read-write access to create/update what it shares. It
    // must be upgraded, not left duplicated alongside the auto-added entry
    // (Home Assistant's Supervisor rejects a `map:` with the same target twice).
    [Fact]
    public void Generate_WithSharedPaths_UpgradesExistingReadOnlyShareMapEntry()
    {
        var analysis = RepoAnalyzer.Analyze(new RepoSourceFiles { DockerfileContent = SharedPathDockerfile });
        var request = new AddonGenerationRequest
        {
            Slug = "proxy", Name = "Proxy", Description = "desc",
            SourceRepoUrl = "https://github.com/example/proxy", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = SharedPathDockerfile,
            SharedPaths = ["/data/nginx/logs"],
            MapEntries = ["share:ro"],
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        var map = Assert.IsType<List<object>>(manifest["map"]);
        Assert.Contains("share:rw", map);
        Assert.DoesNotContain("share:ro", map);
        Assert.Single(map, m => m.ToString()!.StartsWith("share:", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_NoSharedPaths_DoesNotAddShareMapEntry()
    {
        var files = GenerateFromSample();

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        var map = manifest.TryGetValue("map", out var m) ? Assert.IsType<List<object>>(m) : [];
        Assert.DoesNotContain(map, e => e.ToString()!.StartsWith("share:", StringComparison.Ordinal));
    }

    // A path can only ever be symlinked to one target - letting it be listed
    // as both would mean the second call silently no-ops (addon_forge_persist
    // sees the symlink the first call already made and leaves it pointed at
    // the first target), so this must be rejected loudly instead.
    [Fact]
    public void Generate_PathListedAsBothPersistentAndShared_Throws()
    {
        var analysis = RepoAnalyzer.Analyze(new RepoSourceFiles { DockerfileContent = SharedPathDockerfile });
        var request = new AddonGenerationRequest
        {
            Slug = "proxy", Name = "Proxy", Description = "desc",
            SourceRepoUrl = "https://github.com/example/proxy", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = SharedPathDockerfile,
            // Not under /data - a path already under /data is only ever valid
            // as a *shared* path (see PersistenceDetector.NormalizePaths'
            // allowUnderData), so it could never trigger this overlap anyway.
            PersistentPaths = ["/var/lib/app/logs"],
            SharedPaths = ["/var/lib/app/logs"],
        };

        Assert.Throws<InvalidOperationException>(() => AddonConfigGenerator.Generate(analysis, request));
    }

    private const string ImageSourceDockerfile = "FROM example/agent:v1.6.3\nENTRYPOINT [\"/docker_start.sh\"]\n";

    // The whole point of a SourceKind.DockerImage addon that needs no options,
    // no config file and no persistence: nothing a build could deliver is
    // actually needed, so the Supervisor should pull the published image
    // directly rather than addon-forge making it build a Dockerfile that
    // achieves nothing beyond what a plain `image:` already would.
    [Fact]
    public void Generate_ImageSourceWithNoOptionsOrPersistence_UsesNativeImageAndSkipsDockerfile()
    {
        var analysis = RepoAnalyzer.Analyze(new RepoSourceFiles { DockerfileContent = ImageSourceDockerfile });
        var request = new AddonGenerationRequest
        {
            Slug = "agent", Name = "Agent", Description = "desc",
            SourceRepoUrl = "docker.io/example/agent", Version = "1.6.3", Ref = "v1.6.3",
            DockerfileContent = ImageSourceDockerfile,
            Image = "example/agent:v1.6.3",
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        Assert.False(files.Files.ContainsKey("Dockerfile"));
        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        Assert.Equal("example/agent:v1.6.3", manifest["image"]);
    }

    // As soon as anything needs delivering that only a build step can
    // achieve (an option here), the native `image:` shortcut no longer
    // applies - falls back to building the synthetic Dockerfile with the
    // entrypoint wrapped, exactly like any other addon.
    [Fact]
    public void Generate_ImageSourceWithOptions_BuildsDockerfileInsteadOfNativeImage()
    {
        const string dockerfileWithEnv =
            "FROM example/agent:v1.6.3\nENV TZ=UTC\nENTRYPOINT [\"/docker_start.sh\"]\n";
        var analysis = RepoAnalyzer.Analyze(new RepoSourceFiles { DockerfileContent = dockerfileWithEnv });
        var request = new AddonGenerationRequest
        {
            Slug = "agent", Name = "Agent", Description = "desc",
            SourceRepoUrl = "docker.io/example/agent", Version = "1.6.3", Ref = "v1.6.3",
            DockerfileContent = dockerfileWithEnv,
            Image = "example/agent:v1.6.3",
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        Assert.True(files.Files.ContainsKey("Dockerfile"));
        Assert.True(files.Files.ContainsKey(EntrypointWrapperGenerator.ScriptFileName));
        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        Assert.False(manifest.ContainsKey("image"));
    }

    [Fact]
    public void Generate_ArchOverride_UsesProvidedArchitectures()
    {
        var analysis = RepoAnalyzer.Analyze(new RepoSourceFiles { DockerfileContent = ImageSourceDockerfile });
        var request = new AddonGenerationRequest
        {
            Slug = "agent", Name = "Agent", Description = "desc",
            SourceRepoUrl = "docker.io/example/agent", Version = "1.6.3", Ref = "v1.6.3",
            DockerfileContent = ImageSourceDockerfile,
            Image = "example/agent:v1.6.3",
            Arch = ["amd64", "arm64"],
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        var arch = Assert.IsType<List<object>>(manifest["arch"]);
        Assert.Equal(["amd64", "arm64"], arch.Select(a => a.ToString()));
    }

    [Fact]
    public void Generate_NoArchOverride_DefaultsToAmd64Only()
    {
        var files = GenerateFromSample();

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        var arch = Assert.IsType<List<object>>(manifest["arch"]);
        Assert.Equal(["amd64"], arch.Select(a => a.ToString()));
    }

    // Regression coverage for a real gap: A reverse proxy image declares no
    // EXPOSE/ExposedPorts at all (it's built around network_mode: host), so
    // analysis alone would produce an addon with literally no ports at all.
    [Fact]
    public void Generate_ManualPortsWithNoDetectedPorts_AreWrittenToConfigYaml()
    {
        var sourceFiles = new RepoSourceFiles { DockerfileContent = "FROM alpine\nENTRYPOINT [\"/entrypoint.sh\"]\n" };
        var analysis = RepoAnalyzer.Analyze(sourceFiles);
        var request = new AddonGenerationRequest
        {
            Slug = "proxy", Name = "Proxy", Description = "desc",
            SourceRepoUrl = "docker.io/example/proxy", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = sourceFiles.DockerfileContent,
            HostNetwork = true,
            ManualPorts = ["80/tcp:80:HTTP", "443/tcp:443:HTTPS", "443/udp:443:HTTP/3 (QUIC)", "81/tcp:81:Admin UI"],
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);

        Assert.True(Convert.ToBoolean(manifest["host_network"]));
        var ports = Assert.IsType<Dictionary<object, object>>(manifest["ports"]);
        Assert.Equal(80, Convert.ToInt32(ports["80/tcp"]));
        Assert.Equal(443, Convert.ToInt32(ports["443/tcp"]));
        Assert.Equal(443, Convert.ToInt32(ports["443/udp"]));
        Assert.Equal(81, Convert.ToInt32(ports["81/tcp"]));

        var descriptions = Assert.IsType<Dictionary<object, object>>(manifest["ports_description"]);
        Assert.Equal("Admin UI", descriptions["81/tcp"]);

        Assert.Contains("host_network: true", files.Files["DOCS.md"]);
        Assert.Contains("informational only", files.Files["DOCS.md"]);
    }

    [Fact]
    public void Generate_ManualPortConflictsWithDetectedPort_ManualWins()
    {
        const string dockerfile = "FROM node:20-alpine\nEXPOSE 8080\nCMD [\"node\", \"server.js\"]\n";
        var sourceFiles = new RepoSourceFiles { DockerfileContent = dockerfile };
        var analysis = RepoAnalyzer.Analyze(sourceFiles);
        var request = new AddonGenerationRequest
        {
            Slug = "my_app", Name = "My App", Description = "desc",
            SourceRepoUrl = "https://github.com/example/my-app", Version = "1.0.0", Ref = "v1.0.0",
            DockerfileContent = dockerfile,
            ManualPorts = ["8080/tcp:9090"],
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        var ports = Assert.IsType<Dictionary<object, object>>(manifest["ports"]);
        Assert.Equal(9090, Convert.ToInt32(ports["8080/tcp"]));
    }

    [Fact]
    public void Generate_NoHostNetwork_WritesFalseAndOmitsNetworkingDocsSection()
    {
        var files = GenerateFromSample();

        var deserializer = new DeserializerBuilder().Build();
        var manifest = deserializer.Deserialize<Dictionary<object, object>>(files.Files["config.yaml"]);
        Assert.False(Convert.ToBoolean(manifest["host_network"]));
        Assert.DoesNotContain("## Networking", files.Files["DOCS.md"]);
    }
}
