using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Mission;

[GameRequest("/mission/obtain/daily")]
public class ObtainDaily : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqObtainDailyMissionReward req = await ReadData<ReqObtainDailyMissionReward>();
        User user = GetUser();

        if (req.TidList.Any(id => GameData.Instance.TriggerTable.GetValueOrDefault(id)?.HandlerType == HandlerType.Ranking))
            throw new BadHttpRequestException("Ranking rewards are delivered by mail.", 400);

        var response = MissionProgressDelivery.Apply(user, () => Claim(user, req));
        await WriteDataAsync(response);
    }

    private static ResObtainDailyMissionReward Claim(User user, ReqObtainDailyMissionReward req)
    {
        GuildActivity.EnsureMember(user, DateTime.UtcNow);
        user.ResetDataIfNeeded();

        ResObtainDailyMissionReward response = new();

        List<NetRewardData> rewards = [];

        int total_points = 0;

        foreach (int item in req.TidList)
        {
            if (user.ResetableData.CompletedDailyMissions.Contains(item))
            {
                Logging.WriteLine("already completed daily mission", LogType.Warning);
                continue;
            }

            if (!GameData.Instance.TriggerTable.TryGetValue(item, out TriggerRecord? key)) throw new Exception("unknown TID");

            user.ResetableData.CompletedDailyMissions.Add(item);

            if (key.RewardId != 0)
            {
                // Actual reward
                RewardRecord rewardRecord = GameData.Instance.GetRewardTableEntry(key.RewardId) ?? throw new Exception("unable to lookup reward");
                rewards.Add(RewardUtils.RegisterRewardsForUser(user, rewardRecord));
            }
            else
            {
                // Point reward
                total_points += key.PointValue;
            }
        }

        user.AddTrigger(Trigger.PointRewardDaily, total_points);
        user.ResetableData.DailyMissionPoints += total_points;
        GuildActivity.Record(user, total_points, DateTime.UtcNow);

        response.Reward = NetUtils.MergeRewards(rewards, user);
        response.EventBonusReward = new() { PassPoint = new() };
        response.Reward.PassPoint = new();

        return response;
    }
}
