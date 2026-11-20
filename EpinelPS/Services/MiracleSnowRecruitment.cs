using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;

namespace EpinelPS.Services;

public sealed record RecruitPeriod(DateTimeOffset Start, DateTimeOffset End)
{
    public bool IsOpen(DateTime nowUtc) => Start <= nowUtc && nowUtc < End;
}

/// <summary>
/// Standalone 21-day recruitment pools that are opened on their own, outside any lobby banner group.
/// Periods are saved in <see cref="CoreInfo.MiracleSnowRecruitment"/> (name kept for save compatibility).
/// </summary>
internal static class MiracleSnowRecruitment
{
    // Keyed by GachaTypeTable ID -> its PickupGachaEvent.
    //  101/102: Rupee: Winter Shopper / Anis: Miracle Fairy legacy pickups using the "event_old" sprite table.
    //  10090:   winter rerun select-up (event_260917 art, base_gacha_type_pickup_return_04 page), whose
    //           choices include the same two characters. Its assets exist in the current client.
    private static readonly Dictionary<int, int> Events = new() { [101] = 70005, [102] = 70006, [10090] = 70150 };

    // Pools whose banner/page assets are missing from the current client.
    private static readonly HashSet<int> MissingClientArt = [101, 102];

    // The current client has no "event_old" sprite table, so advertising 101/102 leaves the Home view
    // stuck while it builds lobby banners. Keep those two offline (saved periods untouched) until matching
    // client resources are available. Pools with complete client assets are not affected.
    internal static bool Suspended { get; set; } = true;

    /// <summary>Adds a 21-day period from <paramref name="start"/> for each pool that has none; existing periods are kept.</summary>
    internal static void Initialize(CoreInfo state, DateTimeOffset start)
    {
        var periods = state.MiracleSnowRecruitment is { } saved ? new Dictionary<int, RecruitPeriod>(saved) : [];
        bool changed = state.MiracleSnowRecruitment == null;
        foreach (int poolId in Events.Keys)
            if (!periods.ContainsKey(poolId)) { periods[poolId] = new RecruitPeriod(start, start.AddDays(21)); changed = true; }
        if (changed) state.MiracleSnowRecruitment = periods;
    }

    internal static bool IsScheduledPool(int poolId) => Events.ContainsKey(poolId);
    internal static bool IsScheduledEvent(int eventId) => Events.ContainsValue(eventId);
    private static bool IsBlocked(int poolId) => Suspended && MissingClientArt.Contains(poolId);

    internal static bool IsOpen(int poolId, DateTime nowUtc) =>
        !IsBlocked(poolId) && JsonDb.Instance.MiracleSnowRecruitment?.GetValueOrDefault(poolId)?.IsOpen(nowUtc) == true;

    internal static IEnumerable<(int PoolId, NetEventData Event)> OpenPools(DateTime nowUtc)
    {
        foreach (var (poolId, eventId) in Events)
        {
            if (IsBlocked(poolId)) continue;
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
