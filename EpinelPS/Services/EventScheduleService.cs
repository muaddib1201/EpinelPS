using EpinelPS.Data;
using EpinelPS.Models;

namespace EpinelPS.Services;

public static class EventScheduleService
{
    public static int? GetRootEventId(int eventId)
    {
        var data = GameData.Instance;
        var roots = data.LobbyPrivateBannerTable.Values.Select(b => b.EventId).ToHashSet();
        int? DirectRoot(int id)
        {
            var queue = new Queue<int>();
            var visited = new HashSet<int>();
            queue.Enqueue(id);
            while (queue.TryDequeue(out int next))
            {
                if (next <= 0 || !visited.Add(next)) continue;
                if (roots.Contains(next)) return next;
                if (!data.eventManagers.TryGetValue(next, out var row)) continue;
                queue.Enqueue(row.ParentsEventId);
                queue.Enqueue(row.SetField);
            }
            return null;
        }

        int? RecruitmentRoot(int id)
        {
            var direct = DirectRoot(id);
            if (direct != null) return direct;
            if (!data.eventManagers.TryGetValue(id, out var row) ||
                row.EventSystemType is not (EventSystemType.PickupGachaEvent or EventSystemType.BoxGachaEvent or EventSystemType.LoginEvent) ||
                row.EventBannerResourceTable?.StartsWith("event_", StringComparison.Ordinal) != true) return null;
            // Matches EventHelper's resource-table association, but do not guess between two roots.
            var matches = data.eventManagers.Values.Where(e => e.EventBannerResourceTable == row.EventBannerResourceTable)
                .Select(e => DirectRoot(e.Id)).Where(root => root != null).Distinct().Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        var root = RecruitmentRoot(eventId);
        if (root != null) return root;
        var paybackRoots = data.GachaPaybackRecords.Values.Where(p => p.EventId == eventId)
            .Select(p => data.gachaTypes.GetValueOrDefault(p.GachaId))
            .Where(g => g != null).Select(g => RecruitmentRoot(g!.EventId))
            .Where(id => id != null).Distinct().Take(2).ToArray();
        return paybackRoots.Length == 1 ? paybackRoots[0] : null;
    }

    public static DateTime? GetConfiguredEndUtc(CoreInfo state, int rootEventId)
    {
        if (state.EventEndUtc.TryGetValue(rootEventId, out var value))
            return value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        // Existing MARIA-n official schedule. Explicit administration settings take precedence.
        return rootEventId == 83500 ? new DateTime(2026, 11, 18, 19, 59, 59, DateTimeKind.Utc) : null;
    }

    public static DateTime GetEndUtc(CoreInfo state, int eventId, DateTime nowUtc, int legacyDays = 30) =>
        GetRootEventId(eventId) is int root && GetConfiguredEndUtc(state, root) is DateTime end
            ? end : nowUtc.AddDays(legacyDays);

    // Callers hold JsonDb.SaveLock. Replace the dictionary so readers never enumerate a mutated instance.
    public static void SetEndUtc(CoreInfo state, int rootEventId, DateTime endUtc) =>
        state.EventEndUtc = new Dictionary<int, DateTime>(state.EventEndUtc) { [rootEventId] = endUtc };

    public static void EnsureDefaultEndUtc(CoreInfo state, int rootEventId, DateTime nowUtc)
    {
        if (GetConfiguredEndUtc(state, rootEventId) == null) SetEndUtc(state, rootEventId, nowUtc.AddDays(14));
    }
}
