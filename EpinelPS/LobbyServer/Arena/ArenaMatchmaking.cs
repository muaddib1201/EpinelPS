using System.Security.Cryptography;
using System.Text;
using EpinelPS.Data;

namespace EpinelPS.LobbyServer.Arena;

internal sealed partial class ArenaService
{
    private User[][] SpecialRivalGroups()
    {
        if (Own.SpecialRank <= 1) return [];
        var higher = Participants(true).Where(u => u.Arena!.SpecialRank > 0 && u.Arena.SpecialRank < Own.SpecialRank).ToArray();
        if (higher.Length == 0) return [];

        int Priority(int tier) => GameData.Instance.ArenaTiers.TryGetValue(tier, out var row) ? row.Priority : int.MaxValue;
        int ownPriority = Priority(Tier(user, true));
        // Tier priorities are not consecutive (Challenger 10 -> Diamond 1 is 10 -> 101).
        var groups = higher.GroupBy(u => Tier(u, true))
            .Select(group => new { Priority = Priority(group.Key), Players = group.ToArray() })
            .Where(group => group.Priority < ownPriority)
            .OrderByDescending(group => group.Priority).Take(3).OrderBy(group => group.Priority)
            .Select(group => group.Players).ToArray();
        if (groups.Length > 0) return groups;

        // Sparse offline ladders may have only higher positions within the same division.
        // Keep those positions reachable without ever filling a slot from below the viewer.
        return higher.OrderByDescending(u => u.Arena!.SpecialRank).ThenBy(u => u.ID).Take(3)
            .OrderBy(u => u.Arena!.SpecialRank).Select(u => new[] { u }).ToArray();
    }

    private IEnumerable<User> SpecialRivals()
    {
        var groups = SpecialRivalGroups();
        if (groups.Length == 0) return [];
        long cursor = Math.Max(0, Own.SpecialRefresh);
        Own.SpecialRefresh = cursor == long.MaxValue ? 0 : cursor + 1;
        return groups.Select(group =>
        {
            int tier = Tier(group[0], true);
            var ordered = group.OrderBy(u => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"special:{user.ID}:{World.Season}:{tier}:{u.ID}"))), StringComparer.Ordinal)
                .ThenBy(u => u.ID).ToArray();
            return ordered[(int)(cursor % ordered.Length)];
        }).OrderBy(u => u.Arena!.SpecialRank).ThenBy(u => u.ID).ToArray();
    }

    // Local approximation: official score ranges/weights are not published. Do not gate rookie opponents by CP.
    private IEnumerable<User> RookieRivals()
    {
        var participants = Participants(false);
        int ownIndex = participants.FindIndex(u => u.ID == user.ID);
        var rivals = participants.Where(u => u.ID != user.ID).ToArray();
        if (rivals.Length == 0) return [];

        long cursor = Math.Max(0, Own.RookieRefresh);
        Own.RookieRefresh = cursor == long.MaxValue ? 0 : cursor + 1;
        var chosen = new List<User>(3);
        var selected = new HashSet<ulong>();

        void Pick(IEnumerable<User> pool, string role)
        {
            // A per-viewer/season permutation avoids always starting at the same IDs, while the persisted
            // cursor visits every member of a stable rank group before that group's first slot repeats.
            var ordered = pool
                .OrderBy(u => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"rookie:{user.ID}:{World.Season}:{role}:{u.ID}"))), StringComparer.Ordinal)
                .ThenBy(u => u.ID).ToArray();
            if (ordered.Length == 0) return;
            int start = (int)(cursor % ordered.Length);
            // Rotate the full group before skipping a rival already in another slot. Filtering first
            // changes the modulo and can permanently hide members when the near slot alternates.
            for (int offset = 0; offset < ordered.Length; offset++)
            {
                var rival = ordered[(start + offset) % ordered.Length];
                if (!selected.Add(rival.ID)) continue;
                chosen.Add(rival);
                return;
            }
        }

        IEnumerable<User> Nearest()
        {
            var available = rivals.Where(u => !selected.Contains(u.ID)).ToArray();
            if (available.Length == 0) return [];
            long gap = available.Min(u => Math.Abs((long)u.Arena!.Rating - Own.Rating));
            return available.Where(u => Math.Abs((long)u.Arena!.Rating - Own.Rating) == gap);
        }

        Pick(participants.Take(ownIndex), "higher");
        Pick(Nearest(), "near");
        Pick(participants.Skip(ownIndex + 1).Where(u => u.ID != user.ID), "lower");
        while (chosen.Count < Math.Min(3, rivals.Length)) Pick(Nearest(), "fill");
        return chosen.OrderByDescending(u => u.Arena!.Rating).ThenBy(u => u.ID).ToArray();
    }
}
