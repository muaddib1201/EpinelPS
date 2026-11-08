namespace EpinelPS.LobbyServer.Ranking;

[GameRequest("/ranking/get/museumtotal/rankinglist")]
public class GetMuseumTotalRanking : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqGetMuseumTotalRankingList>();
        await WriteDataAsync(RankingService.MuseumTotalBoard(GetUser(), request.Type));
    }
}

[GameRequest("/ranking/get/museumgroup/rankinglist")]
public class GetMuseumGroupRanking : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqGetMuseumGroupRankingList>();
        await WriteDataAsync(RankingService.MuseumGroupBoard(GetUser(), request.Type, request.ConditionId));
    }
}

[GameRequest("/ranking/get/museumtotal/rankachievement")]
public class GetMuseumTotalAchievements : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqGetMuseumTotalRankAchievementLeaderBoard>();
        var board = RankingService.MuseumAchievements(GetUser(), request.Type);
        await WriteDataAsync(new ResGetMuseumTotalRankAchievementLeaderBoard { RankAchievement = { board.RankAchievement } });
    }
}

[GameRequest("/ranking/get/museumgroup/rankachievement")]
public class GetMuseumGroupAchievements : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqGetMuseumGroupRankAchievementLeaderBoard>();
        var board = RankingService.MuseumAchievements(GetUser(), request.Type, request.ConditionId);
        await WriteDataAsync(new ResGetMuseumGroupRankAchievementLeaderBoard { RankAchievement = { board.RankAchievement } });
    }
}

[GameRequest("/ranking/obtain/museumtotal/rankachievementserverreward")]
public class ObtainMuseumTotalServerReward : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqObtainMuseumTotalRankAchievementServerReward>();
        await WriteDataAsync(new ResObtainMuseumTotalRankAchievementServerReward { Reward = RankingService.ClaimMuseum(GetUser(), request.Type) });
    }
}

[GameRequest("/ranking/obtain/museumgroup/rankachievementserverreward")]
public class ObtainMuseumGroupServerReward : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqObtainMuseumGroupRankAchievementServerReward>();
        await WriteDataAsync(new ResObtainMuseumGroupRankAchievementServerReward { Reward = RankingService.ClaimMuseum(GetUser(), request.Type, request.ConditionId) });
    }
}
