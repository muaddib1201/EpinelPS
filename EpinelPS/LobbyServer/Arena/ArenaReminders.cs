using EpinelPS.Database;
using Google.Protobuf.WellKnownTypes;

namespace EpinelPS.LobbyServer.Arena;

internal sealed partial class ArenaService
{
    private static DateTime BadgeClock(DateTime utc) =>
        JsonDb.Instance.Arena is { } world && world.LastUtc > utc ? world.LastUtc : utc;

    private static DateTime? LastBoxClaim(ArenaPlayer arena)
    {
        // Existing saves already record every successful claim, newest first.
        if (arena.RewardHistory.FirstOrDefault() is not { } bytes) return null;
        var claim = NetSpecialArenaRewardHistoryLog.Parser.ParseFrom(bytes);
        return claim.SpecialArenaTicket > 0 ? claim.AccumulateEndAt?.ToDateTime() : null;
    }

    private static bool RookieReminderDue(User player, DateTime dayStart)
    {
        return player.Arena is { RookieJoinedUtc: not null } arena && ArenaTeams.Unlocked(player, false)
            && arena.RookieReminderSeenUtc.GetValueOrDefault() < dayStart
            && (arena.Day < DayAt(dayStart) || arena.RookieCount < RookieFreePlays);
    }

    private static bool SpecialReminderDue(User player, DateTime at, DateTime dayStart, DateTime? claimedAt)
    {
        if (player.Arena is not { SpecialJoinedUtc: not null } arena || !ArenaTeams.Unlocked(player, true)
            || arena.SpecialReminderSeenUtc >= dayStart || claimedAt >= dayStart) return false;

        int capacity = BoxCapacity;
        long available = ClaimableChips(ProjectSpecialRewards(player, at).Chips);
        return capacity > 0 && available * 2m >= capacity;
    }

    private void AcknowledgeReminder(bool special)
    {
        // The overview prefetches both /get endpoints. The selected mode then requests
        // its own enemy list, even when the player is rank one and the list is empty.
        var dayStart = NextDay(now).AddDays(-1);
        if (special)
        {
            if (SpecialReminderDue(user, now, dayStart, LastBoxClaim(Own))) Own.SpecialReminderSeenUtc = now;
        }
        else if (RookieReminderDue(user, dayStart)) Own.RookieReminderSeenUtc = now;
    }

    internal static NetContentEnterTimeData? ArenaBadgeEntry(User player, DateTime utc)
    {
        lock (JsonDb.SaveLock)
        {
            if (player.Arena is not { } arena || (arena.RookieJoinedUtc == null && arena.SpecialJoinedUtc == null)) return null;
            DateTime at = BadgeClock(utc);
            DateTime dayStart = NextDay(at).AddDays(-1);
            DateTime? claimedAt = LastBoxClaim(arena);
            bool due = RookieReminderDue(player, dayStart) || SpecialReminderDue(player, at, dayStart, claimedAt);

            // This badge message has one Arena day marker. Send an explicit old day
            // when a reward newly crosses half capacity after the earlier all-clear.
            // The projection only affects this badge response, never gameplay or history.
            var marker = due ? DateTime.UnixEpoch : new[] {
                dayStart, arena.OverviewEnteredUtc.GetValueOrDefault(), arena.RookieReminderSeenUtc.GetValueOrDefault(),
                arena.SpecialReminderSeenUtc.GetValueOrDefault(), claimedAt.GetValueOrDefault()
            }.Max();
            return new NetContentEnterTimeData { ContentType = NetEnteredContentType.Arena, EnteredAt = Timestamp.FromDateTime(marker) };
        }
    }

    internal static void RecordArenaOverview(User player, DateTime utc)
    {
        lock (player)
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.GetUser(player.ID) != player || player.Arena is not { } arena) return;
            DateTime at = BadgeClock(utc);
            if (arena.OverviewEnteredUtc >= NextDay(at).AddDays(-1)) return;
            var previous = arena.OverviewEnteredUtc;
            arena.OverviewEnteredUtc = at;
            try { JsonDb.Save(); }
            catch { arena.OverviewEnteredUtc = previous; throw; }
        }
    }
}
