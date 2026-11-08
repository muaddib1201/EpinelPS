using EpinelPS.Data;

namespace EpinelPS.LobbyServer.Outpost.Dispatch;

public class DispatchHelper
{
    public static T SelectItemByProbability<T>(List<T> items, Func<T, double> probabilitySelector)
    {
        if (!items.Any())
            return default;

        double totalProb = items.Sum(probabilitySelector);

        if (totalProb <= 0)
            return default;

        Random random = new Random();
        double randomValue = random.NextDouble() * totalProb;

        double cumulativeProb = 0;

        foreach (var item in items)
        {
            cumulativeProb += probabilitySelector(item);

            if (randomValue <= cumulativeProb)
            {
                return item;
            }
        }

        return items.Last();
    }

    public static void SyncDispatchLevel(User user)
    {
        var lessons = user.CompletedTacticAcademyLessons.Distinct()
            .Select(GameData.Instance.GetTacticAcademyLesson).Where(l => l != null).ToArray();
        int Level(LessonType type, int initial) => lessons.Where(l => l.LessonType == type)
            .SelectMany(l => l.LessonReward ?? []).Where(r => r.LessonRewardValue > 0)
            .Select(r => r.LessonRewardId).Append(initial).Max();
        user.DispatchLv = Level(LessonType.Dispatch, 1);
        user.DispatchCollectionLv = Level(LessonType.DispatchCollection, 0);
        user.DispatchFavoriteLv = Level(LessonType.DispatchFavorite, 0);
    }

    private static IEnumerable<DispatchBoardRecord> Boards(User user) => GameData.Instance.DispatchBoardTable.Values.Where(b =>
        b.DispatchType == DispatchType.Dispatch && b.DispatchBoardLv == user.DispatchLv ||
        b.DispatchType == DispatchType.DispatchCollection && user.DispatchCollectionLv > 0 && b.DispatchBoardLv == user.DispatchCollectionLv ||
        b.DispatchType == DispatchType.DispatchFavorite && user.DispatchFavoriteLv > 0 && b.DispatchBoardLv == user.DispatchFavoriteLv);

    public static int GetBaseDispatchCount(User user)
    {
        SyncDispatchLevel(user);
        return Boards(user).Sum(b => b.DispatchMax);
    }

    private static int Capacity(User user, DispatchBoardRecord board) => Math.Max(0, board.DispatchMax +
        (board.DispatchType == DispatchType.Dispatch
            ? GameData.Instance.GetInfracoreGrade(user.InfraCoreLvl)?.FunctionList?.ElementAtOrDefault(1)?.Function ?? 0 : 0));

    // Caller holds the player/save transaction. Claimed and running tasks occupy
    // their slots before pending rows are kept, repaired or rerolled.
    internal static void PrepareBoard(User user, DateTime now, bool reroll = false)
    {
        SyncDispatchLevel(user);
        var boards = Boards(user).ToArray();
        if (!boards.Any(b => b.DispatchType == DispatchType.Dispatch))
            throw new BadHttpRequestException("Dispatch board configuration is unavailable.", 400);
        user.ResetableData.DispatchCount = boards.Sum(b => Capacity(user, b));
        var rows = user.UserDispatchData.dispatchDatas ?? [];
        int day = user.GetDateDay();
        if (user.UserDispatchData.Today != day)
        {
            rows = rows.Where(r => r.IsRun == 1).ToList();
            user.DispatchClearList.Clear();
            user.DispatchResetCount = 0;
            user.UserDispatchData.Today = day;
        }

        var definitions = GameData.Instance.DispatchTable;
        var claimed = user.DispatchClearList.ToHashSet();
        var kept = rows.Where(r => r.IsRun == 1).DistinctBy(r => r.Tid).ToList();
        var occupied = claimed.Concat(kept.Select(r => r.Tid)).ToHashSet();
        foreach (var board in boards)
        {
            int finished = claimed.Count(tid => definitions.TryGetValue(tid, out var d) && d.DispatchType == board.DispatchType);
            int running = kept.Count(r => definitions.TryGetValue(r.Tid, out var d) && d.DispatchType == board.DispatchType);
            int remaining = Math.Max(0, Capacity(user, board) - finished - running);
            var pending = rows.Where(r => r.IsRun == 0 && definitions.TryGetValue(r.Tid, out var d) &&
                d.DispatchType == board.DispatchType).DistinctBy(r => r.Tid).ToArray();
            int slot = 0;
            if (!reroll)
            {
                foreach (var row in pending)
                {
                    if (slot == remaining) break;
                    if (!occupied.Add(row.Tid)) continue;
                    kept.Add(row);
                    slot++;
                }
            }
            for (; slot < remaining; slot++)
            {
                int oldTid = slot < pending.Length ? pending[slot].Tid : 0;
                var available = definitions.Values.Where(d => d.DispatchType == board.DispatchType && !occupied.Contains(d.Id)).ToArray();
                var groups = board.DispatchList.Where(g => g.DispatchProb > 0 && available.Any(d => d.DispatchGroup == g.DispatchGroup)).ToList();
                var group = SelectItemByProbability(groups, g => g.DispatchProb)
                    ?? throw new BadHttpRequestException("Dispatch task pool is exhausted.", 400);
                var candidates = available.Where(d => d.DispatchGroup == group.DispatchGroup).ToArray();
                var alternatives = candidates.Where(d => d.Id != oldTid).ToArray();
                if (alternatives.Length > 0) candidates = alternatives;
                var chosen = candidates[Random.Shared.Next(candidates.Length)];
                occupied.Add(chosen.Id);
                kept.Add(new NetUserDispatchData { Tid = chosen.Id, StartAt = now.Ticks, EndAt = now.AddMinutes(chosen.TimeMin).Ticks });
            }
        }
        user.UserDispatchData.dispatchDatas = kept;
        var visible = kept.Select(r => r.Tid).Concat(claimed).ToHashSet();
        user.SelectableDispatchData.RemoveAll(r => !visible.Contains(r.SelectTid));
    }

    internal static ResResetDispatchList ResetBoard(User user)
    {
        var now = DateTime.UtcNow;
        PrepareBoard(user, now);
        if (user.UserDispatchData.dispatchDatas.Any(r => r.IsRun == 0))
        {
            var price = GameData.Instance.DispatchResetTable.Values.Where(r => r.Id <= (long)user.DispatchResetCount + 1)
                .OrderByDescending(r => r.Id).FirstOrDefault()
                ?? throw new BadHttpRequestException("Dispatch reset price is unavailable.", 400);
            if (price.CurrencyValue < 0 || user.DispatchResetCount == int.MaxValue)
                throw new BadHttpRequestException("Invalid dispatch reset price or count.", 400);
            long balance = Math.Max(0, user.Currency.GetValueOrDefault(price.CurrencyType));
            long first = Math.Min(balance, price.CurrencyValue);
            long rest = price.CurrencyValue - first;
            if (rest > 0 && (price.CurrencyType != CurrencyType.FreeCash || user.Currency.GetValueOrDefault(CurrencyType.ChargeCash) < rest))
                throw new BadHttpRequestException("Insufficient currency to reset dispatch tasks.", 400);
            PrepareBoard(user, now, reroll: true);
            user.Currency[price.CurrencyType] = balance - first;
            if (rest > 0) user.Currency[CurrencyType.ChargeCash] -= rest;
            user.DispatchResetCount++;
        }
        var response = new ResResetDispatchList { DispatchResetCount = user.DispatchResetCount };
        response.DispatchList.AddRange(user.UserDispatchData.dispatchDatas.Select(r => r.Clone()));
        response.Currencies.AddRange(user.Currency.Select(c => new NetUserCurrencyData { Type = (int)c.Key, Value = c.Value }));
        return response;
    }
}
