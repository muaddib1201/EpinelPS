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
                var db = scope.ServiceProvider.GetRequiredService<GameContext>();
                ManagedNpcService.Recover(db);
                LobbyServer.Ranking.RankingService.ObserveSafely();
                ManagedNpcService.UpgradeBreakthroughs();
                ManagedNpcService.UpgradeGrowth();
                ManagedNpcService.UpgradeRosters(DateTime.UtcNow);
                ManagedNpcService.UpgradeSocial(DateTime.UtcNow);
                ManagedNpcService.RefreshProfileRewards(db);
                // Settles NPCs one at a time and yields the save lock between them,
                // so player requests are not queued behind the whole population.
                await ManagedNpcService.TickAsync(DateTime.UtcNow, stoppingToken);
                LobbyServer.Ranking.RankingService.ObserveSafely();
            }
            catch (Exception ex) { logger.LogError(ex, "NPC 托管更新失败，将在下一轮重试。"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
