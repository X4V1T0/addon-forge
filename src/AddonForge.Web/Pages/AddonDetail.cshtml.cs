using AddonForge.Core.Abstractions;
using AddonForge.Core.Analysis;
using AddonForge.Core.Models;
using AddonForge.Core.Orchestration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonForge.Web.Pages;

public class AddonDetailModel(
    IManagedAddonRepository repository,
    IAddonFileWriter fileWriter,
    AddonUpdateService updateService) : PageModel
{
    /// <summary>
    /// Staging/published now hold a whole build context (the source repo's
    /// tree), not just what addon-forge generates — only preview the content
    /// of the handful of files addon-forge itself writes; everything else is
    /// just listed by path.
    /// </summary>
    private static readonly HashSet<string> PreviewableFiles =
        new(StringComparer.OrdinalIgnoreCase) { "config.yaml", "DOCS.md", "CHANGELOG.md", "Dockerfile" };

    public ManagedAddon? Addon { get; private set; }

    public IReadOnlyList<string> PublishedFileList { get; private set; } = [];

    public IReadOnlyList<string> StagedFileList { get; private set; } = [];

    public IReadOnlyDictionary<string, string> PublishedPreviews { get; private set; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> StagedPreviews { get; private set; } = new Dictionary<string, string>();

    /// <summary>Plenty for an icon; catches an accidental wrong-file upload rather than a legitimate one.</summary>
    private const long MaxIconBytes = 2 * 1024 * 1024;

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        Addon = await repository.GetBySlugAsync(slug, ct);
        if (Addon is null)
        {
            return NotFound();
        }

        PublishedFileList = await fileWriter.ListPublishedFilesAsync(slug, ct);
        StagedFileList = await fileWriter.ListStagedFilesAsync(slug, ct);

        PublishedPreviews = await ReadPreviewableAsync(slug, PublishedFileList, fileWriter.ReadPublishedFileAsync, ct);
        StagedPreviews = await ReadPreviewableAsync(slug, StagedFileList, fileWriter.ReadStagedFileAsync, ct);

        return Page();
    }

    public async Task<IActionResult> OnPostCheckNowAsync(string slug, CancellationToken ct)
    {
        var result = await updateService.CheckAndRegenerateAsync(slug, ct);
        TempData["StatusMessage"] = result.LastError is not null
            ? $"Error checking for updates: {result.LastError}"
            : !string.IsNullOrEmpty(result.StagedRef)
                ? $"Update staged: {result.StagedRef}"
                : "Already up to date.";
        return RedirectToPage(new { slug });
    }

    public async Task<IActionResult> OnPostPublishAsync(string slug, CancellationToken ct)
    {
        try
        {
            await updateService.PublishStagedAsync(slug, ct);
            TempData["StatusMessage"] =
                "Written to the local addon store. In Home Assistant, go to Settings → Add-ons → " +
                "Add-on Store → Check for updates to see it.";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Error: {ex.Message}";
        }

        return RedirectToPage(new { slug });
    }

    /// <summary>Serves the addon's current published icon.png so it can be previewed inline — not a text file, so <see cref="IAddonFileWriter.ReadPublishedFileAsync"/> won't return it.</summary>
    public async Task<IActionResult> OnGetIconAsync(string slug, CancellationToken ct)
    {
        var bytes = await fileWriter.ReadPublishedBinaryFileAsync(slug, "icon.png", ct);
        return bytes is null ? NotFound() : File(bytes, "image/png");
    }

    /// <summary>
    /// For repos analysis couldn't find an icon for (or got wrong). Saves the
    /// upload and regenerates immediately so it's actually written to the
    /// local addon store, not just recorded for next time.
    /// </summary>
    public async Task<IActionResult> OnPostSetIconAsync(string slug, IFormFile? icon, CancellationToken ct)
    {
        if (icon is not { Length: > 0 })
        {
            TempData["StatusMessage"] = "No file selected.";
            return RedirectToPage(new { slug });
        }

        if (icon.Length > MaxIconBytes)
        {
            TempData["StatusMessage"] = "Icon file is too large (max 2 MB).";
            return RedirectToPage(new { slug });
        }

        try
        {
            using var ms = new MemoryStream();
            await icon.CopyToAsync(ms, ct);
            await fileWriter.SetManualIconAsync(slug, ms.ToArray(), ct);
            await updateService.RegenerateAsync(slug, ct);
            TempData["StatusMessage"] = "Icon updated and written to the local addon store.";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Error: {ex.Message}";
        }

        return RedirectToPage(new { slug });
    }

    public async Task<IActionResult> OnPostSetPolicyAsync(string slug, UpdatePolicy policy, CancellationToken ct)
    {
        var addon = await repository.GetBySlugAsync(slug, ct);
        if (addon is null)
        {
            return NotFound();
        }

        addon.UpdatePolicy = policy;
        await repository.UpdateAsync(addon, ct);
        TempData["StatusMessage"] = $"Update policy set to {policy}.";
        return RedirectToPage(new { slug });
    }

    /// <summary>
    /// Saves manually-added env vars (for anything a repo only documents in
    /// prose, not in its Dockerfile/compose/.env.example) and regenerates
    /// immediately so the change is actually written to the local addon
    /// store rather than waiting for the next real upstream update.
    /// </summary>
    public async Task<IActionResult> OnPostSetManualEnvVarsAsync(string slug, string? manualEnvVars, CancellationToken ct)
    {
        var addon = await repository.GetBySlugAsync(slug, ct);
        if (addon is null)
        {
            return NotFound();
        }

        addon.ManualEnvVars = ParseManualEnvVars(manualEnvVars);
        await repository.UpdateAsync(addon, ct);

        try
        {
            await updateService.RegenerateAsync(slug, ct);
            TempData["StatusMessage"] = "Extra environment variables saved and written to the local addon store.";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Saved, but regeneration failed: {ex.Message}";
        }

        return RedirectToPage(new { slug });
    }

    private static List<string> ParseManualEnvVars(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// For settings this app only reads from a config file, not env vars
    /// (e.g. a dashboard app's weather config) — addon-forge has no way to
    /// discover that on its own. Also carries the opt-in distroless-debug-shell
    /// toggle, since both exist to get options/a config file into a container
    /// that otherwise can't receive them. Saves and regenerates immediately,
    /// same as extra env vars and the icon.
    /// </summary>
    public async Task<IActionResult> OnPostSetConfigFileAsync(
        string slug, string? configFileTemplate, string? configFileDestPath, bool allowDistrolessDebugShell, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configFileTemplate) != string.IsNullOrWhiteSpace(configFileDestPath))
        {
            TempData["StatusMessage"] = "Config file template and destination path must be given together.";
            return RedirectToPage(new { slug });
        }

        var addon = await repository.GetBySlugAsync(slug, ct);
        if (addon is null)
        {
            return NotFound();
        }

        addon.ConfigFileTemplate = string.IsNullOrWhiteSpace(configFileTemplate) ? null : configFileTemplate;
        addon.ConfigFileDestPath = string.IsNullOrWhiteSpace(configFileDestPath) ? null : configFileDestPath;
        addon.AllowDistrolessDebugShell = allowDistrolessDebugShell;
        await repository.UpdateAsync(addon, ct);

        try
        {
            await updateService.RegenerateAsync(slug, ct);
            TempData["StatusMessage"] = "Config file saved and written to the local addon store.";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Saved, but regeneration failed: {ex.Message}";
        }

        return RedirectToPage(new { slug });
    }

    /// <summary>
    /// Saves which container paths are kept across rebuilds (symlinked into
    /// /data) and regenerates immediately. <paramref name="addDetected"/>
    /// merges in every strongly-detected path first - the one-click way to
    /// act on the "data at risk" warning. Saving at all (even an empty list)
    /// marks the choice as reviewed, which is what silences that warning.
    /// </summary>
    public async Task<IActionResult> OnPostSetPersistentPathsAsync(
        string slug, string? persistentPaths, bool addDetected, CancellationToken ct)
    {
        var addon = await repository.GetBySlugAsync(slug, ct);
        if (addon is null)
        {
            return NotFound();
        }

        var requested = string.IsNullOrWhiteSpace(persistentPaths)
            ? []
            : persistentPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (addDetected)
        {
            requested.AddRange(addon.DetectedPersistentPaths.Where(c => c.Recommended).Select(c => c.Path));
        }

        addon.PersistentPaths = PersistenceDetector.NormalizePaths(requested);
        addon.PersistenceReviewed = true;
        await repository.UpdateAsync(addon, ct);

        var ignored = requested.Count(r => !string.IsNullOrWhiteSpace(r)) - addon.PersistentPaths.Count;
        var note = ignored > 0
            ? $" ({ignored} entr{(ignored == 1 ? "y was" : "ies were")} ignored: not an absolute directory path, a system path, or nested inside another persisted path.{NameRejected(requested, allowUnderData: false)})"
            : "";

        try
        {
            await updateService.RegenerateAsync(slug, ct);
            TempData["StatusMessage"] =
                $"Persistent data paths saved ({addon.PersistentPaths.Count}) and written to the local addon store.{note}";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Saved, but regeneration failed: {ex.Message}";
        }

        return RedirectToPage(new { slug });
    }

    /// <summary>
    /// Saves which container paths are handed to a *different* addon
    /// (symlinked into /share/&lt;slug&gt;/... rather than /data) and
    /// regenerates immediately. Unlike persistent paths, nothing here is
    /// auto-detected - which path is worth exposing, and to whom, is
    /// entirely the user's call.
    /// </summary>
    public async Task<IActionResult> OnPostSetSharedPathsAsync(string slug, string? sharedPaths, CancellationToken ct)
    {
        var addon = await repository.GetBySlugAsync(slug, ct);
        if (addon is null)
        {
            return NotFound();
        }

        var requested = string.IsNullOrWhiteSpace(sharedPaths)
            ? []
            : sharedPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        addon.SharedPaths = PersistenceDetector.NormalizePaths(requested, allowUnderData: true);
        await repository.UpdateAsync(addon, ct);

        var ignored = requested.Count(r => !string.IsNullOrWhiteSpace(r)) - addon.SharedPaths.Count;
        var note = ignored > 0
            ? $" ({ignored} entr{(ignored == 1 ? "y was" : "ies were")} ignored: not an absolute directory path, a system path, or nested inside another shared path.{NameRejected(requested, allowUnderData: true)})"
            : "";

        try
        {
            await updateService.RegenerateAsync(slug, ct);
            TempData["StatusMessage"] =
                $"Shared paths saved ({addon.SharedPaths.Count}) and written to the local addon store.{note}";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Saved, but regeneration failed: {ex.Message}";
        }

        return RedirectToPage(new { slug });
    }

    /// <summary>
    /// Saves host networking + manually-declared ports (for an image that
    /// declares no EXPOSE/ExposedPorts at all, common for one built around
    /// `network_mode: host`) and regenerates immediately.
    /// </summary>
    public async Task<IActionResult> OnPostSetNetworkingAsync(
        string slug, bool hostNetwork, string? manualPorts, CancellationToken ct)
    {
        var addon = await repository.GetBySlugAsync(slug, ct);
        if (addon is null)
        {
            return NotFound();
        }

        addon.HostNetwork = hostNetwork;
        addon.ManualPorts = string.IsNullOrWhiteSpace(manualPorts)
            ? []
            : [.. manualPorts.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        await repository.UpdateAsync(addon, ct);

        try
        {
            await updateService.RegenerateAsync(slug, ct);
            TempData["StatusMessage"] = "Networking saved and written to the local addon store.";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Saved, but regeneration failed: {ex.Message}";
        }

        return RedirectToPage(new { slug });
    }

    /// <summary>
    /// Names the entries that were rejected on their own (an entry dropped
    /// only for being a duplicate or nested inside another one isn't listed,
    /// since that one is still in effect). "N entries were ignored" alone
    /// once hid a real problem: a legitimate path was dropped and nothing
    /// said which.
    /// </summary>
    private static string NameRejected(IEnumerable<string> requested, bool allowUnderData)
    {
        var rejected = requested
            .Where(r => !string.IsNullOrWhiteSpace(r) && PersistenceDetector.NormalizePaths([r], allowUnderData).Count == 0)
            .Select(r => $"'{r.Trim()}'")
            .ToList();

        return rejected.Count == 0 ? "" : " Rejected: " + string.Join(", ", rejected) + ".";
    }

    /// <summary>Silences the "data at risk" warning for a false positive, without changing anything about the addon.</summary>
    public async Task<IActionResult> OnPostDismissDataRiskAsync(string slug, CancellationToken ct)
    {
        var addon = await repository.GetBySlugAsync(slug, ct);
        if (addon is null)
        {
            return NotFound();
        }

        addon.PersistenceReviewed = true;
        await repository.UpdateAsync(addon, ct);
        TempData["StatusMessage"] = "Data-at-risk warning dismissed. Nothing about the addon was changed.";
        return RedirectToPage(new { slug });
    }

    /// <summary>Unlike just forgetting it, this actually removes /addons/&lt;slug&gt; from disk. Irreversible.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(string slug, CancellationToken ct)
    {
        await updateService.DeleteAsync(slug, ct);
        TempData["StatusMessage"] = $"Deleted {slug} from the local addon store.";
        // Index.cshtml.cs explains why this can't be RedirectToPage("/Index") -
        // asp-page-handler resolving to root's bare PathBase (no trailing
        // slash) 404s behind Home Assistant's ingress proxy.
        return LocalRedirect($"{Request.PathBase}/");
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadPreviewableAsync(
        string slug,
        IReadOnlyList<string> allFiles,
        Func<string, string, CancellationToken, Task<string?>> read,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        foreach (var relativePath in allFiles.Where(f => PreviewableFiles.Contains(f)))
        {
            var content = await read(slug, relativePath, ct);
            if (content is not null)
            {
                result[relativePath] = content;
            }
        }

        return result;
    }
}
