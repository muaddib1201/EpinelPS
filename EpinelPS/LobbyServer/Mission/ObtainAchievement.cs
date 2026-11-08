using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Mission;

[GameRequest("/mission/obtain/achievement")]
public class ObtainAchievement : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqObtainAchievementReward req = await ReadData<ReqObtainAchievementReward>();
        User user = GetUser();

        ResObtainAchievementReward response = new();

        // Ranking special rewards are mailed by ranking settlement, not claimed here.
        if (req.TidList.Any(id => GameData.Instance.TriggerTable.GetValueOrDefault(id)?.HandlerType == HandlerType.Ranking))
            throw new BadHttpRequestException("Ranking rewards are delivered by mail.", 400);

        List<NetRewardData> rewards = [];

        int total_points = 0;

        foreach (int item in req.TidList)
        {
            if (user.CompletedAchievements.Contains(item)) continue;

            if (!GameData.Instance.TriggerTable.TryGetValue(item, out TriggerRecord? key)) throw new Exception("unknown TID");

            RewardRecord rewardRecord = GameData.Instance.GetRewardTableEntry(key.RewardId) ?? throw new Exception("unable to lookup reward");

            NetRewardData reward = RewardUtils.RegisterRewardsForUser(user, rewardRecord);
            rewards.Add(reward);

            user.CompletedAchievements.Add(item);

            total_points++;
        }

        user.AddTrigger(Trigger.PointRewardAchievement, total_points);

        response.Reward = NetUtils.MergeRewards(rewards, user);

        JsonDb.Save();

        await WriteDataAsync(response);
    }
}
