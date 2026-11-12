using EpinelPS.LobbyServer.Soloraid;
using EpinelPS.Utils;

namespace EpinelPS.Services;

internal sealed class SoloRaidSeasonSyncWorker(SoloRaidSeasonCatalog catalog, IHttpClientFactory clients) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                try
                {
                    if (!GameConfig.Root.SoloRaidAutoSync) continue;
                    using var client = clients.CreateClient("SoloRaidNikkeHub");
                    bool success = await catalog.RefreshAsync(client, DateTimeOffset.UtcNow, stoppingToken);
                    var status = SoloRaidSeasonSource.GetStatus();
                    Logging.WriteLine("[SoloRaidSync] " + status.Message,
                        success && !status.NeedsGameDataUpdate ? LogType.Info : LogType.Warning);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { Logging.WriteLine("[SoloRaidSync] 同步失败，保留已确认赛季：" + ex.Message, LogType.Warning); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
