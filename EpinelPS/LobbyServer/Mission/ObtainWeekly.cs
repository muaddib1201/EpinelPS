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
        var definitions = new List<TriggerRecord>();
        foreach (int tid in req.TidList.Distinct())
        {
            if (!GameData.Instance.TriggerTable.TryGetValue(tid, out var record) || record.HandlerType != HandlerType.MissionWeekly)
                throw new BadHttpRequestException("Unknown weekly mission.", 400);
            if (!user.WeeklyResetableData.CompletedWeeklyMissions.Contains(tid)) definitions.Add(record);
        }
        var now = DateTime.UtcNow;
        var start = now.Date.AddDays(-((int)now.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7)
            .AddHours(JsonDb.Instance.ResetHourUtcTime);
        if (now < start) start = start.AddDays(-7);
        long storedStart = start.AddHours(9).Ticks, storedEnd = start.AddDays(7).AddHours(9).Ticks;
        using var db = GameContext.CreateNew();
        var progress = db.Triggers.Where(t => t.UserId == user.ID && t.CreatedAt >= storedStart && t.CreatedAt < storedEnd).ToArray()
            .Concat(PendingTriggers(user))
            .Where(t => t.CreatedAt >= storedStart && t.CreatedAt < storedEnd)
            .DistinctBy(t => (t.Type, t.ConditionId, t.CreatedAt, t.Value)).ToArray();
        int points = 0;
        foreach (var record in definitions.Where(r => r.Trigger != Trigger.PointRewardWeekly))
        {
            if (progress.Where(t => t.Type == record.Trigger && t.ConditionId == record.ConditionId).Sum(t => (long)t.Value) < record.ConditionValue)
                throw new BadHttpRequestException("Weekly mission is not complete.", 400);
            points = checked(points + record.PointValue);
        }
        int availablePoints = checked(user.WeeklyResetableData.WeeklyMissionPoints + points);
        if (definitions.Any(r => r.Trigger == Trigger.PointRewardWeekly && availablePoints < r.ConditionValue))
            throw new BadHttpRequestException("Insufficient weekly mission points.", 400);

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
