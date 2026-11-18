using Google.Protobuf.WellKnownTypes;

namespace EpinelPS.LobbyServer.Arena;

internal static class ChampionArenaAvailability
{
    // This is an availability window, not a scheduled tournament. Keep it stable
    // across entry and badge requests so the client does not keep reloading a season.
    internal static NetArenaBanInfo BanInfo() => new()
    {
        Description = "冠军竞技场尚未开放",
        StartAt = Timestamp.FromDateTime(DateTime.UnixEpoch),
        EndAt = Timestamp.FromDateTime(new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    };

    internal static NetChampionArenaBadgeData BadgeData() => new()
    {
        ChampionArenaContentsState = ChampionArenaContentsState.Closed,
        Schedule = new(),
        NextSchedule = new(),
        CurrentOrLastSeasonStartAt = Timestamp.FromDateTime(DateTime.UnixEpoch)
    };

    internal static ResGetChampionArena Entry()
    {
        var badge = BadgeData();
        return new()
        {
            IsBan = true,
            BanInfo = BanInfo(),
            ChampionArenaContentsState = ChampionArenaContentsState.Closed,
            Schedule = badge.Schedule.Clone(),
            BadgeData = badge
        };
    }
}
