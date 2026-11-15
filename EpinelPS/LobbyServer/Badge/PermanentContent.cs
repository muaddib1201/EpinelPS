using EpinelPS.LobbyServer.Arena;
using EpinelPS.LobbyServer.Soloraid;

namespace EpinelPS.LobbyServer.Badge;

[GameRequest("/badge/permanentcontent")]
public class PermanentContent : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqPermanentContentBadgeData req = await ReadData<ReqPermanentContentBadgeData>();
        User user = GetUser();

        ResPermanentContentBadgeData response = new();
        response.ChampionArenaBadgeData = ChampionArenaAvailability.BadgeData();
        response.SoloRaidMuseumBadgeData = new();
        response.TodaySoloRaidNormalPlayCount = SoloRaidHelper.GetTodayNormalPlayCount(user);
        var clock = ctx!.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        if (ArenaService.ArenaBadgeEntry(user, clock.GetUtcNow().UtcDateTime) is { } arena)
            response.ContentEnterTimes.Add(arena);

        await WriteDataAsync(response);
    }
}
