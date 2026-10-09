using AddonForge.Core.Abstractions;
using AddonForge.Core.Orchestration;
using AddonForge.Web.Options;
using Microsoft.Extensions.Options;

namespace AddonForge.Web.Services;

/// <summary>
/// Periodically checks every managed addon's source repo for a new
/// tag/commit and regenerates+stages (and, for Auto-policy addons, publishes)
/// as needed. Never calls the Home Assistant Supervisor — "publish" here only
/// means "copy files into the local addon store folder"; the user still has
/// to hit "check for updates" in HA themselves.
/// </summary>
public class VersionCheckScheduler(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<SchedulerOptions> options,
    ILogger<VersionCheckScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAllAsync(stoppingToken);

            var interval = TimeSpan.FromHours(Math.Max(1, options.CurrentValue.CheckIntervalHours));
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IManagedAddonRepository>();
        var updateService = scope.ServiceProvider.GetRequiredService<AddonUpdateService>();

        var addons = await repository.GetAllAsync(ct);
        logger.LogInformation("Checking {Count} managed addon(s) for upstream updates.", addons.Count);

        foreach (var addon in addons)
        {
            try
            {
                var result = await updateService.CheckAndRegenerateAsync(addon.Slug, ct);
                if (result.LastError is not null)
                {
                    logger.LogWarning("Check failed for {Slug}: {Error}", addon.Slug, result.LastError);
                }
                else if (!string.IsNullOrEmpty(result.StagedRef))
                {
                    logger.LogInformation("Update staged for {Slug}: {Ref}", addon.Slug, result.StagedRef);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unexpected error checking {Slug}.", addon.Slug);
            }
        }
    }
}
