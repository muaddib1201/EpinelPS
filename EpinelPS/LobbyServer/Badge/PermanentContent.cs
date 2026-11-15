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
        // TODO

        await WriteDataAsync(response);
    }
}
