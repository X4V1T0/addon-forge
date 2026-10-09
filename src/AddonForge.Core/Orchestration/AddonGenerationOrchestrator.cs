using AddonForge.Core.Abstractions;
using AddonForge.Core.Analysis;
using AddonForge.Core.Generation;
using AddonForge.Core.Models;

namespace AddonForge.Core.Orchestration;

/// <summary>
/// Everything the caller decided, ahead of cloning: what to build and what to
/// call it. CloneRef and DisplayRef are deliberately separate: `git clone
/// --branch` only accepts an actual branch/tag name, never a bare commit SHA
/// — but for a branch-tracked addon, the ref worth recording/showing (in
/// config.yaml's version, DOCS.md, CHANGELOG.md) is the resolved commit SHA,
/// not just "main" again. For tag tracking the two are always the same value.
/// </summary>
public record NewAddonInput(
    string RepoUrl,
    string CloneRef,
    string DisplayRef,
    string Slug,
    string Name,
    string Description,
    string Version,
    bool Ingress,
    int? IngressPort,
    string? DockerfilePath = null,
    IReadOnlyList<string>? MapEntries = null,
    IReadOnlyList<string>? ManualEnvVars = null,
    string? ConfigFileTemplate = null,
    string? ConfigFileDestPath = null,
    bool AllowDistrolessDebugShell = false,
    IReadOnlyList<string>? PersistentPaths = null,
    IReadOnlyList<string>? SharedPaths = null,
    SourceKind SourceKind = SourceKind.GitRepo,
    bool HostNetwork = false,
    IReadOnlyList<string>? ManualPorts = null);

public record AddonGenerationOutcome(RepoAnalysisResult Analysis, GeneratedAddonFiles Files);

/// <summary>
/// Ties together IGitClient (clone) or IContainerRegistryClient (inspect a
/// published image - see SourceKind), RepoAnalyzer/ImageSourceGenerator
/// (deterministic extraction), LlmSchemaAssistant (optional refinement of
/// ambiguous env vars) and AddonConfigGenerator (file assembly) into the one
/// operation both the "add new addon" flow and the version-check scheduler
/// need. LLM assistance only ever runs when analysis actually flagged
/// something ambiguous, and its own retry/fallback means a missing or
/// unreachable LLM endpoint degrades to the deterministic (manual-review)
/// result rather than failing the request.
/// </summary>
public class AddonGenerationOrchestrator(
    IGitClient gitClient, ILlmClient llmClient, IAddonFileWriter fileWriter, IContainerRegistryClient registryClient)
{
    public async Task<RepoAnalysisResult> AnalyzeAsync(
        string repoUrl, string refName, string? dockerfilePath, CancellationToken ct)
    {
        await using var repo = await gitClient.ShallowCloneAsync(repoUrl, refName, ct);
        var sourceFiles = WithChosenDockerfile(repo, repo.ReadSourceFiles(), dockerfilePath);
        var candidates = sourceFiles.DockerfileContent is null || !string.IsNullOrWhiteSpace(dockerfilePath)
            ? repo.FindDockerfileCandidates()
            : [];
        return await AnalyzeAndRefineAsync(sourceFiles, candidates, ct);
    }

    /// <summary>
    /// The SourceKind.DockerImage equivalent of <see cref="AnalyzeAsync"/>:
    /// inspects the published image directly (no clone at all) and runs the
    /// same ambiguous-env-var LLM refinement pass a git-sourced repo gets.
    /// <paramref name="imageReference"/> has no tag of its own (e.g.
    /// "docker.io/example/agent") - <paramref name="tag"/> is which
    /// published version to inspect.
    /// </summary>
    public async Task<RepoAnalysisResult> AnalyzeImageAsync(string imageReference, string tag, CancellationToken ct)
    {
        var reference = ImageReference.Parse(imageReference).WithTag(tag);
        var inspection = await registryClient.InspectAsync(reference, ct);
        return await RefineAmbiguousEnvVarsAsync(ImageSourceGenerator.BuildRepoAnalysisResult(inspection), ct);
    }

    /// <summary>
    /// Best-effort only: a bare image reference (e.g. "example/proxy") is
    /// never itself a URL, and the Supervisor's own schema rejects
    /// config.yaml's `url:` outright if it isn't one - a real bug found the
    /// hard way. Only Docker Hub and ghcr.io get a constructed link (their
    /// web URL follows a fixed, reliable pattern from the image reference
    /// alone); anywhere else this returns null, which simply omits `url:`
    /// rather than risk writing a link that doesn't resolve.
    /// </summary>
    private static string? BuildImageWebUrl(ImageReference reference) => reference.Registry.ToLowerInvariant() switch
    {
        "docker.io" => $"https://hub.docker.com/r/{reference.Repository}",
        "ghcr.io" => $"https://github.com/{reference.Repository}",
        _ => null,
    };

    /// <summary>
    /// A repo with no root Dockerfile is built from one the user picked
    /// instead. Analysis has to read THAT file (and match compose volumes to
    /// the service building it) - otherwise its VOLUME/ENV/mkdir paths are
    /// never seen, and detection silently reports nothing for exactly the
    /// multi-service repos where it matters most.
    /// </summary>
    private static RepoSourceFiles WithChosenDockerfile(IClonedRepo repo, RepoSourceFiles files, string? dockerfilePath)
    {
        if (files.DockerfileContent is not null || string.IsNullOrWhiteSpace(dockerfilePath))
        {
            return files;
        }

        return new RepoSourceFiles
        {
            DockerfileContent = repo.ReadFile(dockerfilePath),
            DockerfilePath = dockerfilePath,
            ComposeContent = files.ComposeContent,
            EnvExampleContent = files.EnvExampleContent,
            ReadmeContent = files.ReadmeContent,
        };
    }

    /// <summary>
    /// Analyzes, generates config.yaml/DOCS.md/CHANGELOG.md, and stages the
    /// result. Dispatches on <see cref="NewAddonInput.SourceKind"/>: a
    /// GitRepo clones and builds its own Dockerfile (see
    /// <see cref="GenerateFromGitRepoAsync"/>); a DockerImage inspects an
    /// already-published image instead (see
    /// <see cref="GenerateFromImageAsync"/>) - both converge on the same
    /// AddonConfigGenerator.Generate call from there.
    /// </summary>
    public Task<AddonGenerationOutcome> GenerateAsync(NewAddonInput input, CancellationToken ct) =>
        input.SourceKind == SourceKind.DockerImage
            ? GenerateFromImageAsync(input, ct)
            : GenerateFromGitRepoAsync(input, ct);

    /// <summary>
    /// Clones, analyzes, and stages the result — the *whole* cloned tree, not
    /// just the Dockerfile, since a Dockerfile is rarely self-contained (COPY
    /// requirements.txt, COPY package.json, COPY src/ ...) and the Supervisor
    /// builds directly from what's on disk under the addon's folder.
    /// </summary>
    private async Task<AddonGenerationOutcome> GenerateFromGitRepoAsync(NewAddonInput input, CancellationToken ct)
    {
        await using var repo = await gitClient.ShallowCloneAsync(input.RepoUrl, input.CloneRef, ct);
        var sourceFiles = WithChosenDockerfile(repo, repo.ReadSourceFiles(), input.DockerfilePath);

        var dockerfileContent = sourceFiles.DockerfileContent;

        var candidates = dockerfileContent is null ? repo.FindDockerfileCandidates() : [];

        if (dockerfileContent is null)
        {
            var hint = candidates.Count > 0
                ? $" This repo has {candidates.Count} Dockerfile(s) elsewhere instead (e.g. '{candidates[0]}') " +
                  "— set a Dockerfile path to use one of them, rather than silently generating an addon " +
                  "the Supervisor will reject as \"dockerfile is missing\"."
                : " No Dockerfile was found anywhere in this repo, and it can't be built as a single-container addon without one.";

            throw new InvalidOperationException($"No Dockerfile at the root of '{input.RepoUrl}'.{hint}");
        }

        var analysis = await AnalyzeAndRefineAsync(sourceFiles, candidates, ct);

        if (input.ManualEnvVars is { Count: > 0 })
        {
            var manual = EnvExampleParser.Parse(string.Join('\n', input.ManualEnvVars), source: "manual");
            analysis = MergeRefined(analysis, manual);
        }

        if (!string.IsNullOrWhiteSpace(input.ConfigFileTemplate))
        {
            analysis = MergeConfigFileTemplatePlaceholders(analysis, input.ConfigFileTemplate);
        }

        var request = new AddonGenerationRequest
        {
            Slug = input.Slug,
            Name = input.Name,
            Description = input.Description,
            SourceRepoUrl = input.RepoUrl,
            SourceWebUrl = input.RepoUrl,
            Version = input.Version,
            Ref = input.DisplayRef,
            Ingress = input.Ingress,
            IngressPort = input.IngressPort,
            HostNetwork = input.HostNetwork,
            ManualPorts = input.ManualPorts ?? [],
            DockerfileContent = dockerfileContent,
            MapEntries = input.MapEntries ?? [],
            ConfigFileTemplate = input.ConfigFileTemplate,
            ConfigFileDestPath = input.ConfigFileDestPath,
            AllowDistrolessDebugShell = input.AllowDistrolessDebugShell,
            PersistentPaths = input.PersistentPaths ?? [],
            SharedPaths = input.SharedPaths ?? [],
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        if (files.Files.ContainsKey(EntrypointWrapperGenerator.ScriptFileName))
        {
            foreach (var name in EntrypointWrapperGenerator.DockerignoreFileNames)
            {
                if (repo.ReadFile(name) is { } dockerignore)
                {
                    files.Files[name] = EntrypointWrapperGenerator.AllowGeneratedFilesInDockerignore(dockerignore);
                }
            }
        }

        var stagingDir = await fileWriter.PrepareStagingAsync(input.Slug, ct);
        await repo.CopyToAsync(stagingDir, ct);
        await fileWriter.WriteToStagingAsync(input.Slug, files.Files, ct);
        await fileWriter.EnsureIconAsync(input.Slug, ct);
        await fileWriter.ApplyManualIconAsync(input.Slug, ct);

        return new AddonGenerationOutcome(analysis, files);
    }

    /// <summary>
    /// The SourceKind.DockerImage equivalent of
    /// <see cref="GenerateFromGitRepoAsync"/>: no clone, no build context to
    /// stage - just the published image's own manifest/config, inspected
    /// directly, plus a synthesized Dockerfile (see ImageSourceGenerator) that
    /// lets everything downstream of here (AddonConfigGenerator,
    /// EntrypointWrapperGenerator, distroless detection...) treat it exactly
    /// like a git-sourced repo's own Dockerfile.
    /// </summary>
    private async Task<AddonGenerationOutcome> GenerateFromImageAsync(NewAddonInput input, CancellationToken ct)
    {
        var reference = ImageReference.Parse(input.RepoUrl).WithTag(input.CloneRef);
        var inspection = await registryClient.InspectAsync(reference, ct);
        var analysis = await RefineAmbiguousEnvVarsAsync(ImageSourceGenerator.BuildRepoAnalysisResult(inspection), ct);

        if (input.ManualEnvVars is { Count: > 0 })
        {
            var manual = EnvExampleParser.Parse(string.Join('\n', input.ManualEnvVars), source: "manual");
            analysis = MergeRefined(analysis, manual);
        }

        if (!string.IsNullOrWhiteSpace(input.ConfigFileTemplate))
        {
            analysis = MergeConfigFileTemplatePlaceholders(analysis, input.ConfigFileTemplate);
        }

        var request = new AddonGenerationRequest
        {
            Slug = input.Slug,
            Name = input.Name,
            Description = input.Description,
            SourceRepoUrl = input.RepoUrl,
            SourceWebUrl = BuildImageWebUrl(reference),
            Version = input.Version,
            Ref = input.DisplayRef,
            Ingress = input.Ingress,
            IngressPort = input.IngressPort,
            HostNetwork = input.HostNetwork,
            ManualPorts = input.ManualPorts ?? [],
            DockerfileContent = ImageSourceGenerator.BuildSyntheticDockerfile(reference, inspection),
            MapEntries = input.MapEntries ?? [],
            ConfigFileTemplate = input.ConfigFileTemplate,
            ConfigFileDestPath = input.ConfigFileDestPath,
            AllowDistrolessDebugShell = input.AllowDistrolessDebugShell,
            PersistentPaths = input.PersistentPaths ?? [],
            SharedPaths = input.SharedPaths ?? [],
            Image = reference.ToString(),
            Arch = inspection.Architectures,
        };

        var files = AddonConfigGenerator.Generate(analysis, request);

        await fileWriter.PrepareStagingAsync(input.Slug, ct);
        await fileWriter.WriteToStagingAsync(input.Slug, files.Files, ct);
        await fileWriter.EnsureIconAsync(input.Slug, ct);
        await fileWriter.ApplyManualIconAsync(input.Slug, ct);

        return new AddonGenerationOutcome(analysis, files);
    }

    private async Task<RepoAnalysisResult> AnalyzeAndRefineAsync(
        RepoSourceFiles sourceFiles, IReadOnlyList<string> dockerfileCandidates, CancellationToken ct)
    {
        var analysis = RepoAnalyzer.Analyze(sourceFiles);
        if (dockerfileCandidates.Count > 0)
        {
            analysis = CloneWith(analysis, dockerfileCandidates: dockerfileCandidates);
        }

        return await RefineAmbiguousEnvVarsAsync(analysis, ct);
    }

    /// <summary>
    /// Shared by both source kinds: refines whatever env vars analysis
    /// couldn't classify deterministically via the LLM endpoint, if one is
    /// configured - a no-op (and no network call at all) when nothing came
    /// back ambiguous.
    /// </summary>
    private async Task<RepoAnalysisResult> RefineAmbiguousEnvVarsAsync(RepoAnalysisResult analysis, CancellationToken ct)
    {
        if (analysis.AmbiguousEnvVars.Count == 0)
        {
            return analysis;
        }

        var refined = await LlmSchemaAssistant.RefineAsync(llmClient, analysis.AmbiguousEnvVars, analysis.ReadmeExcerpt, ct);
        return refined.Count == 0 ? analysis : MergeRefined(analysis, refined);
    }

    /// <summary>
    /// Adds an option for any `${OPTION_NAME}` in the config file template
    /// that doesn't already correspond to a detected or manually-added one -
    /// otherwise a name used only in the template would never actually show
    /// up in the addon's Configuration tab for the user to set. Existing
    /// candidates are never touched: a placeholder only ever fills a gap,
    /// it doesn't override a real detected/manual definition of the same name.
    /// </summary>
    private static RepoAnalysisResult MergeConfigFileTemplatePlaceholders(RepoAnalysisResult analysis, string configFileTemplate)
    {
        var known = new HashSet<string>(analysis.EnvVars.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        var placeholders = EntrypointWrapperGenerator.ExtractPlaceholderNames(configFileTemplate);
        var missing = placeholders
            .Where(name => !known.Contains(name))
            .Select(name => DockerfileParser.BuildCandidate(name, null, "config file template"))
            .ToList();

        if (missing.Count == 0)
        {
            return analysis;
        }

        return CloneWith(analysis, envVars: [.. analysis.EnvVars, .. missing]);
    }

    private static RepoAnalysisResult MergeRefined(RepoAnalysisResult analysis, IReadOnlyList<EnvVarCandidate> refined)
    {
        var byName = analysis.EnvVars.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var r in refined)
        {
            byName[r.Name] = r;
        }

        return CloneWith(analysis, envVars: [.. byName.Values.OrderBy(v => v.Name, StringComparer.Ordinal)]);
    }

    private static RepoAnalysisResult CloneWith(
        RepoAnalysisResult source,
        IReadOnlyList<EnvVarCandidate>? envVars = null,
        IReadOnlyList<string>? dockerfileCandidates = null) => new()
    {
        HasDockerfile = source.HasDockerfile,
        HasCompose = source.HasCompose,
        Ports = source.Ports,
        Volumes = source.Volumes,
        PersistentPathCandidates = source.PersistentPathCandidates,
        EnvVars = envVars ?? source.EnvVars,
        DockerfileCandidates = dockerfileCandidates ?? source.DockerfileCandidates,
        ReadmeExcerpt = source.ReadmeExcerpt,
    };
}
