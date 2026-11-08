namespace EpinelPS.LobbyServer.Ranking;

[GameRequest("/ranking/getlist")]
public class GetRankingList : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqGetRankingList>();
        await WriteDataAsync(RankingService.Board(GetUser(), request.Type));
    }
}

[GameRequest("/ranking/rankachievement")]
public class GetAchievementLeaderboard : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqGetRankAchievementLeaderBoard>();
        await WriteDataAsync(RankingService.AchievementBoard(GetUser(), request.Type));
    }
}

[GameRequest("/ranking/rankachievementserverreward")]
public class ObtainServerReward : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqObtainRankAchievementServerReward>();
        await WriteDataAsync(new ResObtainRankAchievementServerReward { Reward = RankingService.Claim(GetUser(), request.Type) });
    }
}

[GameRequest("/ranking/allrankachievementserverreward")]
public class ObtainAllServerRewards : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        await ReadData<ReqObtainAllRankAchievementServerReward>();
        await WriteDataAsync(new ResObtainAllRankAchievementServerReward { Reward = RankingService.Claim(GetUser(), null) });
    }
}
