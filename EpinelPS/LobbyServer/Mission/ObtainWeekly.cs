using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Mission;

[GameRequest("/mission/obtain/weekly")]
public class ObtainWeekly : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqObtainWeeklyMissionReward>();
        var user = GetUser();
        var response = MissionProgressDelivery.Apply(user, () => Claim(user, req));
        await WriteDataAsync(response);
    }

    private static ResObtainWeeklyMissionReward Claim(User user, ReqObtainWeeklyMissionReward req)
    {
        user.ResetDataIfNeeded();
        var requested = new List<TriggerRecord>();
        foreach (int tid in req.TidList.Distinct())
        {
            if (!GameData.Instance.TriggerTable.TryGetValue(tid, out var record) || record.HandlerType != HandlerType.MissionWeekly)
            {
                Logging.WriteLine($"[WeeklyMission] User {user.ID} skipped {tid}: not a weekly mission", LogType.Warning);
                continue;
            }
            if (!user.WeeklyResetableData.CompletedWeeklyMissions.Contains(tid)) requested.Add(record);
        }
        var start = WeeklyReset.Start(DateTime.UtcNow);
        long storedStart = start.AddHours(9).Ticks, storedEnd = start.AddDays(7).AddHours(9).Ticks;
        using var db = GameContext.CreateNew();
        var progress = db.Triggers.Where(t => t.UserId == user.ID && t.CreatedAt >= storedStart && t.CreatedAt < storedEnd).ToArray()
            .Concat(PendingTriggers(user))
            .Where(t => t.CreatedAt >= storedStart && t.CreatedAt < storedEnd)
            .DistinctBy(t => (t.Type, t.ConditionId, t.CreatedAt, t.Value)).ToArray();

        // A batch claim pays every finished item; an unfinished one is skipped and
        // logged instead of failing the whole request.
        var definitions = new List<TriggerRecord>();
        int points = 0;
        foreach (var record in requested.Where(r => r.Trigger != Trigger.PointRewardWeekly))
        {
            long value = progress.Where(t => t.Type == record.Trigger && t.ConditionId == record.ConditionId).Sum(t => (long)t.Value);
            if (value < record.ConditionValue)
            {
                Logging.WriteLine($"[WeeklyMission] User {user.ID} skipped {record.Id}: {record.Trigger} progress {value}/{record.ConditionValue} since {start:u}", LogType.Warning);
                continue;
            }
            definitions.Add(record);
            points = checked(points + record.PointValue);
        }
        int availablePoints = checked(user.WeeklyResetableData.WeeklyMissionPoints + points);
        foreach (var record in requested.Where(r => r.Trigger == Trigger.PointRewardWeekly))
        {
            if (availablePoints < record.ConditionValue)
            {
                Logging.WriteLine($"[WeeklyMission] User {user.ID} skipped {record.Id}: points {availablePoints}/{record.ConditionValue}", LogType.Warning);
                continue;
            }
            definitions.Add(record);
        }

        var rewards = new List<NetRewardData>();
        foreach (var record in definitions)
        {
            if (record.RewardId != 0)
            {
                var reward = GameData.Instance.GetRewardTableEntry(record.RewardId)
                    ?? throw new InvalidDataException("Weekly mission reward is missing.");
                rewards.Add(RewardUtils.RegisterRewardsForUser(user, reward));
            }
            user.WeeklyResetableData.CompletedWeeklyMissions.Add(record.Id);
        }
        if (points > 0) user.AddTrigger(Trigger.PointRewardWeekly, points);
        user.WeeklyResetableData.WeeklyMissionPoints = availablePoints;
        var response = new ResObtainWeeklyMissionReward
        {
            Reward = NetUtils.MergeRewards(rewards, user),
            EventBonusReward = new() { PassPoint = new() }
        };
        response.Reward.PassPoint = new();
        return response;
    }

    private static IEnumerable<TriggerModelNew> PendingTriggers(User user)
    {
        foreach (var t in user.PendingMissionTriggers) yield return t;
        foreach (var t in user.Interception?.Receipts.SelectMany(r => r.PendingTriggers) ?? []) yield return t;
        foreach (var t in user.Arena?.PendingTriggers ?? []) yield return t;
        foreach (var receipt in user.CounselMissionReceipts.Where(r => r.PendingTrigger))
            yield return new TriggerModelNew { Type = Trigger.CharacterCounsel, Value = 1, CreatedAt = receipt.CreatedAt };
    }
}
