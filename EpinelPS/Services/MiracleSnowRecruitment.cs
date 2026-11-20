using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;

namespace EpinelPS.Services;

public sealed record RecruitPeriod(DateTimeOffset Start, DateTimeOffset End)
{
    public bool IsOpen(DateTime nowUtc) => Start <= nowUtc && nowUtc < End;
}

internal static class MiracleSnowRecruitment
{
    // Keyed by GachaTypeTable ID. These events share "event_old" with unrelated pools.
    private static readonly Dictionary<int, int> Events = new() { [101] = 70005, [102] = 70006 };

    // The current client has no "event_old" sprite table, so advertising either pickup leaves the
    // Home view stuck while it builds lobby banners. Keep both pools offline (saved periods untouched)
    // until matching client resources are available.
    internal static bool Suspended { get; set; } = true;

    internal static void Initialize(CoreInfo state, DateTimeOffset start)
    {
        state.MiracleSnowRecruitment ??= Events.Keys.ToDictionary(id => id, _ => new RecruitPeriod(start, start.AddDays(21)));
    }

    internal static bool IsScheduledPool(int poolId) => Events.ContainsKey(poolId);
    internal static bool IsScheduledEvent(int eventId) => Events.ContainsValue(eventId);

    internal static bool IsOpen(int poolId, DateTime nowUtc) =>
        !Suspended && JsonDb.Instance.MiracleSnowRecruitment?.GetValueOrDefault(poolId)?.IsOpen(nowUtc) == true;

    internal static IEnumerable<(int PoolId, NetEventData Event)> OpenPools(DateTime nowUtc)
    {
        if (Suspended) yield break;
        foreach (var (poolId, eventId) in Events)
        {
            var period = JsonDb.Instance.MiracleSnowRecruitment?.GetValueOrDefault(poolId);
            if (period?.IsOpen(nowUtc) != true) continue;
            yield return (poolId, new NetEventData
            {
                Id = eventId, EventSystemType = (int)EventSystemType.PickupGachaEvent,
                EventStartDate = period.Start.UtcTicks, EventVisibleDate = period.Start.UtcTicks,
                EventEndDate = period.End.UtcTicks, EventDisableDate = period.End.UtcTicks
            });
        }
    }
}
