namespace EpinelPS.Services;

internal sealed class EventExpiryWorker(ILogger<EventExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { EventScheduleService.GetActiveBannerIds(DateTime.UtcNow); }
            catch (Exception ex) { logger.LogError(ex, "Could not save expired event selections. Will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
