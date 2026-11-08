using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Ranking;
using EpinelPS.Models;
using EpinelPS.Utils;

namespace EpinelPS.Services;

// Maintenance only: the workspace-only executable invokes this on a stopped-save
// copy. No game endpoint or startup migration invokes this account-specific repair.
public static class RankingTowerRepair
{
    private static readonly (CorporationTowerType Tower, Trigger Trigger)[] Companies =
    [
        (CorporationTowerType.ELYSION, Trigger.TowerElysionClear),
        (CorporationTowerType.MISSILIS, Trigger.TowerMissilisClear),
        (CorporationTowerType.TETRA, Trigger.TowerTetraClear),
        (CorporationTowerType.OVERSPEC, Trigger.TowerOverspecClear)
    ];

    public sealed record FloorReward(int Id, int Floor, int RewardId, RewardRecord Definition);
    public sealed record TowerAdvance(CorporationTowerType Tower, int Before, int After, FloorReward[] RewardedFloors);
    public sealed record FirstTransfer(int TargetId, ulong PreviousUser, int PreviousOwnRank, long AchievedAt);
    public sealed record Result(ulong Account, int TargetFloor, TowerAdvance[] Towers, FirstTransfer[] FirstTransfers,
        Dictionary<CurrencyType, long> CurrencyBefore, Dictionary<CurrencyType, long> CurrencyAfter,
        Dictionary<int, long> ItemsBefore, Dictionary<int, long> ItemsAfter, long[] CountsBeforeTransfer, long[] CountsAfterTransfer);

    public static Result Execute(ulong account, int targetFloor)
    {
        var state = JsonDb.Instance;
        var user = state.Users.SingleOrDefault(u => u.ID == account && !u.IsBanned && u.ManagedNpc == null)
            ?? throw new InvalidDataException("The requested human account is unavailable.");
        if (targetFloor < 1) throw new InvalidDataException("Target floor must be positive.");
        ValidateRanks(state);
        using var db = GameContext.CreateNew();
        if (!db.Users.Any(u => u.ID == account)) throw new InvalidDataException("Account has no SQLite profile.");
        var history = db.Triggers.Where(t => t.UserId == account && t.Value > 0).ToArray().Concat(user.PendingMissionTriggers).ToArray();
        var data = GameData.Instance;
        // Validate every tower and reward before the first mutation, including a
        // later tower whose missing row would otherwise leave a partial advance.
        var plan = Companies.Select(company =>
        {
            var floors = data.towerTable.Values.Where(t => t.Type == company.Tower).ToDictionary(t => t.Floor);
            if (!floors.ContainsKey(targetFloor)) throw new InvalidDataException("Missing target floor for " + company.Tower);
            int current = user.TowerProgress.GetValueOrDefault(company.Tower);
            if (current < 0 || current > 0 && !floors.ContainsKey(current)) throw new InvalidDataException("Invalid saved tower floor.");
            foreach (var clear in history.Where(t => t.Type == company.Trigger && t.Value > 0))
                if (data.towerTable.TryGetValue(clear.ConditionId, out var row) && row.Type == company.Tower)
                    current = Math.Max(current, row.Floor);
            var rewards = Enumerable.Range(current + 1, Math.Max(0, targetFloor - current)).Select(floor =>
            {
                if (!floors.TryGetValue(floor, out var row) || !data.RewardDataRecords.TryGetValue(row.RewardId, out var reward))
                    throw new InvalidDataException("Missing tower floor or reward: " + company.Tower + "/" + floor);
                return new FloorReward(row.Id, floor, row.RewardId, reward);
            }).ToArray();
            return new TowerAdvance(company.Tower, current, Math.Max(current, targetFloor), rewards);
        }).ToArray();
        var currencyBefore = new Dictionary<CurrencyType, long>(user.Currency);
        var itemsBefore = ItemCounts(user);
        foreach (var tower in plan)
        {
            user.TowerProgress[tower.Tower] = tower.Before;
            if (tower.RewardedFloors.Length == 0) continue;
            var result = AdminCommands.SkipTowerFloor(account, $"{tower.Tower}-{targetFloor}");
            if (!result.ok) throw new InvalidDataException(result.error);
        }
        // Settle ordinary newly reached tower milestones before transferring old
        // firsts, so only the additional cumulative tiers get manual notifications.
        RankingService.Update(user);
        long[] before = Counts(user.Ranking!);
        var transfers = TransferFirsts(state, account);
        long[] after = Counts(user.Ranking!);
        for (int tier = 0; tier < 3; tier++)
            if (after[tier] > before[tier]) user.AddTrigger((Trigger)(31 + tier), checked((int)(after[tier] - before[tier])));
        JsonDb.Save();
        RankingService.Update(user);
        return new(account, targetFloor, plan, transfers, currencyBefore, new(user.Currency), itemsBefore, ItemCounts(user), before, after);
    }

    public static FirstTransfer[] TransferFirsts(CoreInfo state, ulong account)
    {
        var owner = state.Users.SingleOrDefault(u => u.ID == account)
            ?? throw new InvalidDataException("The requested account is unavailable.");
        ValidateRanks(state);
        var changes = new List<FirstTransfer>();
        foreach (int target in GameData.Instance.Rankings.Keys.Order())
        {
            var entries = state.Users.Where(u => u.Ranking?.Achievements.ContainsKey(target) == true).ToArray();
            var first = entries.SingleOrDefault(u => u.Ranking!.Achievements[target].Rank == 1);
            if (first == null || first == owner) continue;
            var originalFirst = first.Ranking!.Achievements[target];
            long at = originalFirst.AchievedAt;
            owner.Ranking ??= new();
            int oldRank = 0;
            if (owner.Ranking.Achievements.TryGetValue(target, out var own))
            {
                oldRank = own.Rank;
                originalFirst.Rank = oldRank;
                own.Rank = 1;
                own.AchievedAt = at;
            }
            else
            {
                foreach (var player in entries)
                {
                    var previous = player.Ranking!.Achievements[target];
                    if (++previous.Rank > 10) player.Ranking.Achievements.Remove(target);
                }
                owner.Ranking.Achievements[target] = new() { Rank = 1, Score = originalFirst.Score, AchievedAt = at };
            }
            changes.Add(new(target, first.ID, oldRank, at));
        }
        return changes.ToArray();
    }

    private static void ValidateRanks(CoreInfo state)
    {
        if (state.Users.Select(u => u.ID).Distinct().Count() != state.Users.Count) throw new InvalidDataException("Duplicate accounts.");
        foreach (int target in GameData.Instance.Rankings.Keys)
        {
            var ranks = state.Users.Where(u => u.Ranking?.Achievements.ContainsKey(target) == true)
                .Select(u => u.Ranking!.Achievements[target].Rank).ToArray();
            if (ranks.Any(r => r is < 1 or > 10) || ranks.Distinct().Count() != ranks.Length)
                throw new InvalidDataException("Invalid or duplicate achievement places: " + target);
        }
    }

    private static long[] Counts(RankingPlayerState state) => new[] { 1, 5, 10 }.Select((tier, index) =>
        state.LegacyCounts.GetValueOrDefault(31 + index) + state.Achievements.Values.LongCount(a => a.Rank <= tier)).ToArray();
    private static Dictionary<int, long> ItemCounts(User user) => user.Items.GroupBy(i => i.ItemType).ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Count));
}
