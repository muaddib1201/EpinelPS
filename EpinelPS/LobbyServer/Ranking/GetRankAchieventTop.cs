namespace EpinelPS.LobbyServer.Ranking;

[GameRequest("/ranking/rankachievementtop")]
public class GetRankAchieventTop : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqGetRankAchievementTop req = await ReadData<ReqGetRankAchievementTop>();

        ResGetRankAchievementTop response = RankingService.Counts(GetUser());

        await WriteDataAsync(response);
    }
}
