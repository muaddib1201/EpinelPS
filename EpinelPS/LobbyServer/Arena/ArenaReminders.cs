using EpinelPS.Database;
using Google.Protobuf.WellKnownTypes;

namespace EpinelPS.LobbyServer.Arena;

internal sealed partial class ArenaService
{
    private static DateTime BadgeClock(DateTime utc) =>
        JsonDb.Instance.Arena is { } world && world.LastUtc > utc ? world.LastUtc : utc;

    // The permanent badge reply reports the player's last Arena overview visit as recorded.
    // On 2026-11-18/19 every reply that could drive the Ark arena "N" badge was varied in the
    // live client (this entered time, ranking flag, reward box, free attempts in arena and
    // login data, champion data and ban); the badge never changed, so no reminder is faked
    // here. RookieReminderSeenUtc/SpecialReminderSeenUtc stay in saves from the earlier rule.
    internal static NetContentEnterTimeData? ArenaBadgeEntry(User player)
    {
        lock (JsonDb.SaveLock)
        {
            if (player.Arena?.OverviewEnteredUtc is not { } entered) return null;
            return new NetContentEnterTimeData { ContentType = NetEnteredContentType.Arena,
                EnteredAt = Timestamp.FromDateTime(DateTime.SpecifyKind(entered, DateTimeKind.Utc)) };
        }
    }

    internal static void RecordArenaOverview(User player, DateTime utc)
    {
        lock (player)
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.GetUser(player.ID) != player || player.Arena is not { } arena) return;
            DateTime at = BadgeClock(utc);
            var previous = arena.OverviewEnteredUtc;
            if (previous >= at) return;
            arena.OverviewEnteredUtc = at;
            try { JsonDb.Save(); }
            catch { arena.OverviewEnteredUtc = previous; throw; }
        }
    }
}
