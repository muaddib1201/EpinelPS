namespace EpinelPS.LobbyServer.Soloraid;

/// <summary>
/// An explicit season, independent of the request date and the largest preloaded manager ID.
/// Defaults preserve compatibility with existing gameconfig.json files.
/// </summary>
public sealed class SoloRaidSeasonConfig
{
    public int RaidId { get; set; } = 1000044;
    public DateTimeOffset VisibleAt { get; set; } = new(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);
    public DateTimeOffset StartAt { get; set; } = new(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);
    public DateTimeOffset EndAt { get; set; } = new(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);
    // Local result-display policy; this does not distribute ranking rewards.
    public DateTimeOffset SettleAt { get; set; } = new(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);
    public DateTimeOffset DisableAt { get; set; } = new(2026, 11, 25, 20, 0, 0, TimeSpan.Zero);

    public bool IsValid => RaidId > 0 && VisibleAt <= StartAt && StartAt < EndAt &&
                           EndAt <= SettleAt && SettleAt <= DisableAt;

    public NetSoloRaidPeriodData ToNet() => new()
    {
        VisibleDate = VisibleAt.UtcTicks,
        StartDate = StartAt.UtcTicks,
        EndDate = EndAt.UtcTicks,
        SettleDate = SettleAt.UtcTicks,
        DisableDate = DisableAt.UtcTicks,
    };
}

internal sealed class SoloRaidPeriodException() : Exception("Solo raid is outside the configured season");
