using AddonForge.Core.Abstractions;
using AddonForge.Core.Models;
using AddonForge.Core.Orchestration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonForge.Web.Pages;

public class IndexModel(IManagedAddonRepository repository, AddonUpdateService updateService) : PageModel
{
    public IReadOnlyList<ManagedAddon> Addons { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Addons = await repository.GetAllAsync(ct);
    }

    public async Task<IActionResult> OnPostCheckNowAsync(string slug, CancellationToken ct)
    {
        var result = await updateService.CheckAndRegenerateAsync(slug, ct);
        TempData["StatusMessage"] = result.LastError is not null
            ? $"Error checking {slug}: {result.LastError}"
            : !string.IsNullOrEmpty(result.StagedRef)
                ? $"Update found for {slug}: {result.StagedRef}"
                : $"{slug} is up to date.";
        return RedirectToRoot();
    }

    public async Task<IActionResult> OnPostForgetAsync(string slug, CancellationToken ct)
    {
        await repository.DeleteAsync(slug, ct);
        TempData["StatusMessage"] = $"Stopped managing {slug}. Its files under the local addon store were left untouched.";
        return RedirectToRoot();
    }

    /// <summary>Unlike Forget, this actually removes /addons/&lt;slug&gt; from disk. Irreversible.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(string slug, CancellationToken ct)
    {
        await updateService.DeleteAsync(slug, ct);
        TempData["StatusMessage"] = $"Deleted {slug} from the local addon store.";
        return RedirectToRoot();
    }

    /// <summary>
    /// RedirectToPage() resolves to just Request.PathBase with no trailing
    /// slash behind Home Assistant's ingress proxy — which the proxy then
    /// 404s on. Building the URL explicitly guarantees the trailing slash.
    /// </summary>
    private IActionResult RedirectToRoot() => LocalRedirect($"{Request.PathBase}/");
}
