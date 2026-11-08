using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;

namespace EpinelPS.LobbyServer.Lostsector;

internal static class LostSectorMissionProgress
{
    internal static void Recover(User user)
    {
        lock (user)
        lock (JsonDb.SaveLock)
        {
            // The final chest is the durable clear evidence. Opening a field or
            // collecting its separate exploration reward does not prove a clear.
            var cleared = user.LostSectorData
                .Where(s => s.Value.ReceivedFinalReward && GameData.Instance.LostSector.ContainsKey(s.Key))
                .Select(s => s.Key).ToArray();
            if (cleared.Length == 0) return;

            using var db = GameContext.CreateNew();
            var recorded = db.Triggers
                .Where(t => t.UserId == user.ID && t.Type == Trigger.LostSectorClear && t.Value > 0)
                .Select(t => t.ConditionId).ToHashSet();
            recorded.UnionWith(user.PendingMissionTriggers
                .Where(t => t.Type == Trigger.LostSectorClear && t.Value > 0).Select(t => t.ConditionId));
            var missing = cleared.Where(s => !recorded.Contains(s)).ToArray();
            if (missing.Length == 0) return;

            // Commit the notification outbox first. Failed SQLite delivery is
            // retried by startup/trigger sync without repeating any chest payout.
            MissionProgressDelivery.Apply(user, () =>
            {
                foreach (int sector in missing) user.AddTrigger(Trigger.LostSectorClear, 1, sector);
                return true;
            });
        }
    }
}
