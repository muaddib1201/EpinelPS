using System.Security.Cryptography;
using System.Text;

namespace EpinelPS.LobbyServer.Arena;

internal sealed partial class ArenaService
{
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
            var ordered = pool.Where(u => !selected.Contains(u.ID))
                .OrderBy(u => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"rookie:{user.ID}:{World.Season}:{role}:{u.ID}"))), StringComparer.Ordinal)
                .ThenBy(u => u.ID).ToArray();
            if (ordered.Length == 0) return;
            var rival = ordered[(int)(cursor % ordered.Length)];
            chosen.Add(rival);
            selected.Add(rival.ID);
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
