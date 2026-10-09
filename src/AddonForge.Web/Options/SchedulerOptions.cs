using Microsoft.Extensions.Configuration;

namespace AddonForge.Web.Options;

/// <summary>Bound from this addon's own config.yaml `options.scheduler` group.</summary>
public class SchedulerOptions
{
    // See OpenAiCompatibleOptions.ApiKey for why this attribute is needed:
    // the config binder doesn't map "check_interval_hours" to this property
    // name on its own.
    [ConfigurationKeyName("check_interval_hours")]
    public int CheckIntervalHours { get; set; } = 6;
}
