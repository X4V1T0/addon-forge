using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using AddonForge.Core.Abstractions;
using AddonForge.Core.Models;
using AddonForge.Core.Orchestration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonForge.Web.Pages;

public class AddAddonModel(
    AddonGenerationOrchestrator orchestrator,
    AddonUpdateService updateService,
    IManagedAddonRepository repository,
    IAddonFileWriter fileWriter) : PageModel
{
    /// <summary>Plenty for an icon; catches an accidental wrong-file upload rather than a legitimate one.</summary>
    private const long MaxIconBytes = 2 * 1024 * 1024;

    [BindProperty]
    public AddAddonInput Input { get; set; } = new();

    public RepoAnalysisResult? Analysis { get; private set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    /// <summary>Step 1: clone + deterministically analyze the repo, then pre-fill editable defaults.</summary>
    public async Task<IActionResult> OnPostAnalyzeAsync(CancellationToken ct)
    {
        var refName = ResolveRefName();
        if (string.IsNullOrWhiteSpace(Input.RepoUrl) || string.IsNullOrWhiteSpace(refName))
        {
            Error = "Repo URL and ref are required.";
            return Page();
        }

        try
        {
            Analysis = Input.SourceKind == SourceKind.DockerImage
                ? await orchestrator.AnalyzeImageAsync(Input.RepoUrl, refName, ct)
                : await orchestrator.AnalyzeAsync(Input.RepoUrl, refName, NullIfBlank(Input.DockerfilePath), ct);
        }
        catch (Exception ex)
        {
            Error = Input.SourceKind == SourceKind.DockerImage
                ? $"Could not inspect image: {ex.Message}"
                : $"Could not analyze repo: {ex.Message}";
            return Page();
        }

        // For a published image, every detected path IS one of its own
        // VOLUMEs - inherited from the image, so it can't be turned into a
        // symlink (see AddonConfigGenerator), and pre-filling it would only
        // set the user up for a generation error.
        if (string.IsNullOrWhiteSpace(Input.PersistentPaths) && Input.SourceKind != SourceKind.DockerImage)
        {
            Input.PersistentPaths = string.Join('\n', Analysis.PersistentPathCandidates.Where(c => c.Recommended).Select(c => c.Path));
        }

        if (string.IsNullOrWhiteSpace(Input.Slug))
        {
            Input.Slug = SlugFromUrl(Input.RepoUrl);
        }

        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            Input.Name = Input.Slug;
        }

        if (string.IsNullOrWhiteSpace(Input.Description))
        {
            Input.Description = Analysis.ReadmeExcerpt ?? $"Generated from {Input.RepoUrl}";
        }

        if (string.IsNullOrWhiteSpace(Input.Version))
        {
            Input.Version = Input.RefType == SourceRefType.Tag ? Input.Ref.TrimStart('v', 'V') : "0.1.0";
        }

        ModelState.Clear();
        return Page();
    }

    /// <summary>
    /// Step 2: re-clones and regenerates from scratch using the (possibly
    /// user-edited) fields rather than trusting anything carried over from
    /// the preview step, then writes the result straight to the local addon
    /// store — this is an explicit, one-off confirmation, not a background
    /// update, so there's no staging involved.
    /// </summary>
    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken ct)
    {
        var refName = ResolveRefName();

        if (await repository.GetBySlugAsync(Input.Slug, ct) is not null)
        {
            Error = $"An addon with slug '{Input.Slug}' is already managed.";
            Analysis = await SafeAnalyzeAsync(refName, ct);
            return Page();
        }

        Analysis = await SafeAnalyzeAsync(refName, ct);
        if (Analysis is not null && !Analysis.HasDockerfile && string.IsNullOrWhiteSpace(Input.DockerfilePath))
        {
            Error = Analysis.DockerfileCandidates.Count > 0
                ? "No Dockerfile at the repo root — pick one of the paths found below before confirming."
                : "No Dockerfile found anywhere in this repo. It can't be built as a single-container addon.";
            return Page();
        }

        if (Input.IconUpload is { Length: > MaxIconBytes })
        {
            Error = "Icon file is too large (max 2 MB).";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Input.ConfigFileTemplate) != string.IsNullOrWhiteSpace(Input.ConfigFileDestPath))
        {
            Error = "Config file template and destination path must be given together.";
            Analysis = await SafeAnalyzeAsync(refName, ct);
            return Page();
        }

        try
        {
            if (Input.IconUpload is { Length: > 0 })
            {
                using var ms = new MemoryStream();
                await Input.IconUpload.CopyToAsync(ms, ct);
                await fileWriter.SetManualIconAsync(Input.Slug, ms.ToArray(), ct);
            }

            var newAddonInput = new NewAddonInput(
                Input.RepoUrl,
                CloneRef: refName,
                DisplayRef: refName,
                Input.Slug,
                Input.Name,
                Input.Description,
                Input.Version,
                Input.Ingress,
                Input.Ingress ? Input.IngressPort : null,
                DockerfilePath: string.IsNullOrWhiteSpace(Input.DockerfilePath) ? null : Input.DockerfilePath,
                MapEntries: ParseMapEntries(Input.MapEntries),
                ManualEnvVars: ParseManualEnvVars(Input.ManualEnvVars),
                ConfigFileTemplate: string.IsNullOrWhiteSpace(Input.ConfigFileTemplate) ? null : Input.ConfigFileTemplate,
                ConfigFileDestPath: string.IsNullOrWhiteSpace(Input.ConfigFileDestPath) ? null : Input.ConfigFileDestPath,
                AllowDistrolessDebugShell: Input.AllowDistrolessDebugShell,
                PersistentPaths: ParseLines(Input.PersistentPaths),
                SharedPaths: ParseLines(Input.SharedPaths),
                SourceKind: Input.SourceKind,
                HostNetwork: Input.HostNetwork,
                ManualPorts: ParseLines(Input.ManualPorts));

            // A DockerImage addon has no branch concept at all - only tags
            // (see VersionCheckService) - so it's always tracked as Tag,
            // regardless of whatever RefType/TrackedBranch happen to be set to.
            await updateService.CreateAsync(
                newAddonInput,
                Input.UpdatePolicy,
                Input.SourceKind == SourceKind.DockerImage ? SourceRefType.Tag : Input.RefType,
                Input.SourceKind == SourceKind.GitRepo && Input.RefType == SourceRefType.Branch ? Input.TrackedBranch : null,
                ct);
        }
        catch (Exception ex)
        {
            Error = $"Could not generate addon: {ex.Message}";
            return Page();
        }

        TempData["StatusMessage"] =
            $"{Input.Name} was written to the local addon store. In Home Assistant, go to " +
            "Settings → Add-ons → Add-on Store → Check for updates to see it.";
        return RedirectToPage("/AddonDetail", new { slug = Input.Slug });
    }

    private string ResolveRefName() =>
        Input.RefType == SourceRefType.Branch ? (Input.TrackedBranch ?? "main") : Input.Ref;

    private async Task<RepoAnalysisResult?> SafeAnalyzeAsync(string refName, CancellationToken ct)
    {
        try
        {
            return Input.SourceKind == SourceKind.DockerImage
                ? await orchestrator.AnalyzeImageAsync(Input.RepoUrl, refName, ct)
                : await orchestrator.AnalyzeAsync(Input.RepoUrl, refName, NullIfBlank(Input.DockerfilePath), ct);
        }
        catch
        {
            return null;
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<string> ParseLines(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// One HA `map:` entry per line (e.g. "media:rw"). HA's valid targets
    /// are a fixed set the Supervisor enforces on its own — this only trims
    /// and dedupes, it doesn't second-guess what the user typed.
    /// </summary>
    private static List<string> ParseMapEntries(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// One `KEY=value` per line (same convention as a .env.example; leave the
    /// value empty for options with no sensible default, e.g. "API_TOKEN=").
    /// Not deduped by key like map entries are — a repeated key just means
    /// the later line wins once these are merged with the detected env vars.
    /// </summary>
    private static List<string> ParseManualEnvVars(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string SlugFromUrl(string url)
    {
        var trimmed = url.TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        var lastSegment = trimmed.Split('/').LastOrDefault() ?? "addon";
        var slug = Regex.Replace(lastSegment.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return string.IsNullOrEmpty(slug) ? "addon" : slug;
    }

    public class AddAddonInput
    {
        [Display(Name = "Source")]
        public SourceKind SourceKind { get; set; } = SourceKind.GitRepo;

        [Required, Display(Name = "Repo URL / image reference")]
        public string RepoUrl { get; set; } = string.Empty;

        [Display(Name = "Tag to track")]
        public string Ref { get; set; } = string.Empty;

        [Display(Name = "Track by")]
        public SourceRefType RefType { get; set; } = SourceRefType.Tag;

        [Display(Name = "Branch to track")]
        public string? TrackedBranch { get; set; }

        public string Slug { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        [Display(Name = "Dockerfile path")]
        public string? DockerfilePath { get; set; }

        [Display(Name = "Folder mappings")]
        public string? MapEntries { get; set; }

        [Display(Name = "Extra environment variables")]
        public string? ManualEnvVars { get; set; }

        [Display(Name = "Persistent data paths")]
        public string? PersistentPaths { get; set; }

        [Display(Name = "Paths to share with other addons")]
        public string? SharedPaths { get; set; }

        [Display(Name = "Icon")]
        public IFormFile? IconUpload { get; set; }

        [Display(Name = "Config file template")]
        public string? ConfigFileTemplate { get; set; }

        [Display(Name = "Config file destination path")]
        public string? ConfigFileDestPath { get; set; }

        [Display(Name = "Allow a distroless debug shell")]
        public bool AllowDistrolessDebugShell { get; set; }

        public bool Ingress { get; set; }

        [Display(Name = "Ingress port")]
        public int? IngressPort { get; set; } = 8099;

        [Display(Name = "Run on the host network")]
        public bool HostNetwork { get; set; }

        [Display(Name = "Manual ports")]
        public string? ManualPorts { get; set; }

        [Display(Name = "Update policy")]
        public UpdatePolicy UpdatePolicy { get; set; } = UpdatePolicy.Manual;
    }
}
