using EpinelPS.Database;

namespace EpinelPS.Services;

internal sealed class ManagedNpcWorker(IServiceScopeFactory scopes, ILogger<ManagedNpcWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                ManagedNpcService.Recover(scope.ServiceProvider.GetRequiredService<GameContext>());
                ManagedNpcService.UpgradeBreakthroughs();
                ManagedNpcService.UpgradeGrowth();
                ManagedNpcService.UpgradeRosters(DateTime.UtcNow);
                ManagedNpcService.UpgradeSocial(DateTime.UtcNow);
                ManagedNpcService.Tick(DateTime.UtcNow);
            }
            catch (Exception ex) { logger.LogError(ex, "NPC 托管更新失败，将在下一轮重试。"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
