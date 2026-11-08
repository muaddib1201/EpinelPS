using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Mission;

internal static class MissionTriggerHistory
{
    internal static TriggerModelNew[] Read(User user)
    {
        using var db = GameContext.CreateNew();
        return db.Triggers.Where(t => t.UserId == user.ID).ToArray().Concat(Pending(user))
            .DistinctBy(t => (t.Type, t.ConditionId, t.CreatedAt, t.Value)).ToArray();
    }

    internal static IEnumerable<TriggerModelNew> Pending(User user) => user.PendingMissionTriggers
        .Concat(user.PendingMailRewardTriggers)
        .Concat(user.Interception?.Receipts.SelectMany(r => r.PendingTriggers) ?? [])
        .Concat(user.Arena?.PendingTriggers ?? [])
        .Concat(user.CounselMissionReceipts.Where(r => r.PendingTrigger).Select(r => new TriggerModelNew
            { UserId = user.ID, Type = Data.Trigger.CharacterCounsel, Value = 1, CreatedAt = r.CreatedAt }))
        .Concat(RewardMutationScope.Pending(user));
}
