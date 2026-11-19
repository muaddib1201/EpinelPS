using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;

namespace EpinelPS.Services;

public static class EventScheduleService
{
    internal static Snapshot CreateSnapshot(CoreInfo state) => new(GameData.Instance, state.EventEndUtc);

    internal sealed class Snapshot
    {
        private readonly GameData data;
        private readonly IReadOnlyDictionary<int, DateTime> deadlines;
        private readonly HashSet<int> roots;
        private readonly Dictionary<int, List<int>> extraParents = [];
        private readonly Dictionary<int, int?> resolved = [];

        internal Snapshot(GameData data, IReadOnlyDictionary<int, DateTime> deadlines)
        {
            this.data = data;
            this.deadlines = deadlines;
            roots = data.LobbyPrivateBannerTable.Values.Select(b => b.EventId).Where(id => id > 0).ToHashSet();
            void Link(int child, int parent)
            {
                if (child <= 0 || parent <= 0 || child == parent) return;
                if (!extraParents.TryGetValue(child, out var links)) extraParents[child] = links = [];
                links.Add(parent);
            }
            foreach (var group in data.eventManagers.Values
                .Where(e => e.EventBannerResourceTable?.StartsWith("event_", StringComparison.Ordinal) == true)
                .GroupBy(e => e.EventBannerResourceTable))
            {
                var anchors = group.Where(e => e.ParentsEventId > 0 || e.SetField > 0 || roots.Contains(e.Id)).Select(e => e.Id).ToArray();
                foreach (var row in group.Where(e => e.EventSystemType is EventSystemType.PickupGachaEvent or EventSystemType.BoxGachaEvent or EventSystemType.LoginEvent))
                    foreach (int anchor in anchors) Link(row.Id, anchor);
            }
            foreach (var payback in data.GachaPaybackRecords.Values)
                if (data.gachaTypes.TryGetValue(payback.GachaId, out var gacha)) Link(payback.EventId, gacha.EventId);
            foreach (var gacha in data.gachaTypes.Values) Link(gacha.DailyFreeGachaEventId, gacha.EventId);
        }

        private int? FindRoot(int id, bool includeIndirect)
        {
            var queue = new Queue<int>();
            var visited = new HashSet<int>();
            var matches = new HashSet<int>();
            queue.Enqueue(id);
            while (queue.TryDequeue(out int next))
            {
                if (next <= 0 || !visited.Add(next)) continue;
                if (roots.Contains(next)) { matches.Add(next); continue; }
                if (data.eventManagers.TryGetValue(next, out var row))
                {
                    queue.Enqueue(row.ParentsEventId);
                    queue.Enqueue(row.SetField);
                }
                if (includeIndirect && extraParents.TryGetValue(next, out var links))
                    foreach (int parent in links) queue.Enqueue(parent);
            }
            return matches.Count == 1 ? matches.Single() : null;
        }

        internal int? GetRootEventId(int eventId)
        {
            if (!resolved.TryGetValue(eventId, out var root))
                resolved[eventId] = root = FindRoot(eventId, includeIndirect: false) ?? FindRoot(eventId, includeIndirect: true);
            return root;
        }

        internal DateTime? GetFixedEndUtc(int eventId) => GetRootEventId(eventId) is int root ? ConfiguredEnd(deadlines, root) : null;
        internal DateTime GetEndUtc(int eventId, DateTime nowUtc) => GetFixedEndUtc(eventId) ?? nowUtc.AddDays(30);
    }

    public static DateTime? GetConfiguredEndUtc(CoreInfo state, int rootEventId) => ConfiguredEnd(state.EventEndUtc, rootEventId);

    internal static List<int> WithoutExpiredBanners(CoreInfo state, List<int> ids, DateTime nowUtc)
    {
        bool Expired(int id) => GameData.Instance.LobbyPrivateBannerTable.TryGetValue(id, out var banner)
            && GetConfiguredEndUtc(state, banner.EventId) <= nowUtc;
        return ids.Exists(Expired) ? ids.FindAll(id => !Expired(id)) : ids;
    }

    internal static List<int> GetActiveBannerIds(DateTime nowUtc)
    {
        lock (JsonDb.SaveLock)
        {
            var state = JsonDb.Instance;
            var previous = state.ActiveEventBannerIds;
            var active = WithoutExpiredBanners(state, previous, nowUtc);
            if (active == previous) return active;
            state.ActiveEventBannerIds = active;
            try { JsonDb.Save(); }
            catch { state.ActiveEventBannerIds = previous; throw; }
            return active;
        }
    }

    private static DateTime? ConfiguredEnd(IReadOnlyDictionary<int, DateTime> deadlines, int rootEventId)
    {
        if (deadlines.TryGetValue(rootEventId, out var value))
            return value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        // Existing MARIA-n official schedule. Explicit administration settings take precedence.
        return rootEventId == 83500 ? new DateTime(2026, 11, 18, 19, 59, 59, DateTimeKind.Utc) : null;
    }

    public static DateTime? GetFixedEndUtc(CoreInfo state, int eventId) => CreateSnapshot(state).GetFixedEndUtc(eventId);

    public static DateTime GetEndUtc(CoreInfo state, int eventId, DateTime nowUtc) => CreateSnapshot(state).GetEndUtc(eventId, nowUtc);

    // Callers hold JsonDb.SaveLock. Replace the dictionary so readers never enumerate a mutated instance.
    public static void SetEndUtc(CoreInfo state, int rootEventId, DateTime endUtc) =>
        state.EventEndUtc = new Dictionary<int, DateTime>(state.EventEndUtc) { [rootEventId] = endUtc };

    public static void EnsureDefaultEndUtc(CoreInfo state, int rootEventId, DateTime nowUtc)
    {
        if (GetConfiguredEndUtc(state, rootEventId) == null) SetEndUtc(state, rootEventId, nowUtc.AddDays(14));
    }
}
