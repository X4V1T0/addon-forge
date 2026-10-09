using AddonForge.Infrastructure.Llm;
using AddonForge.Infrastructure.Publishing;
using AddonForge.Web.Options;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace AddonForge.Web.Pages;

public class SettingsModel(
    IOptionsMonitor<OpenAiCompatibleOptions> llmOptions,
    IOptionsMonitor<SchedulerOptions> schedulerOptions,
    IOptionsMonitor<FileSystemAddonFileWriterOptions> pathOptions) : PageModel
{
    public OpenAiCompatibleOptions Llm => llmOptions.CurrentValue;

    public SchedulerOptions Scheduler => schedulerOptions.CurrentValue;

    public FileSystemAddonFileWriterOptions Paths => pathOptions.CurrentValue;

    public void OnGet()
    {
    }
}
