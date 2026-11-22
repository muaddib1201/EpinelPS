using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;
using LockRequest = EpinelPS.AwakeningOptionLockReserveRequest;

namespace EpinelPS.LobbyServer.Inventory;

internal static class EquipmentOptionRoll
{
    public static NetEquipmentAwakeningOption? Roll(User user, long isn, LockRequest[] requests,
        IList<NetUserItemData> items, Func<int, int, List<int>, int> rollUnlocked)
    {
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!ReferenceEquals(JsonDb.GetUser(user.ID), user))
                throw new BadHttpRequestException("Stale equipment inventory.", 409);

            var current = user.EquipmentAwakenings.FirstOrDefault(x => x.Isn == isn && !x.IsNewData);
            if (current == null) return null;
            var pending = user.EquipmentAwakenings.FirstOrDefault(x => x.Isn == isn && x.IsNewData);
            if (pending != null) return pending.Option.Clone();

            var original = current.Option;
            int[] ids = [original.Option1Id, original.Option2Id, original.Option3Id];
            bool[] fixedLocks = [original.Option1Lock, original.Option2Lock, original.Option3Lock];
            bool[] oneTime = [original.IsOption1DisposableLock, original.IsOption2DisposableLock, original.IsOption3DisposableLock];
            int oldFixed = 0;
            for (int slot = 0; slot < ids.Length; slot++)
            {
                if (!Enum.IsDefined(requests[slot]) || (ids[slot] == 0 && requests[slot] != LockRequest.NoReserve))
                    return null;

                // Some older saves mark a one-time lock with both flags.
                fixedLocks[slot] = ids[slot] != 0 && fixedLocks[slot] && !oneTime[slot];
                if (fixedLocks[slot]) oldFixed++;
                fixedLocks[slot] |= requests[slot] == LockRequest.ReservePermanentLock;
                oneTime[slot] = ids[slot] != 0 && !fixedLocks[slot] &&
                    (oneTime[slot] || requests[slot] == LockRequest.ReserveDisposableLock);
            }

            var cost = GetCosts(oldFixed, fixedLocks.Count(x => x), oneTime.Count(x => x));
            var debits = new List<(DbItemData Item, int Amount, int Before)>();
            foreach (var (itemId, required) in cost)
            {
                int remaining = required;
                foreach (var stack in user.Items.Where(x => x.ItemType == itemId && x.Count > 0))
                {
                    int take = Math.Min(remaining, stack.Count);
                    debits.Add((stack, take, stack.Count));
                    remaining -= take;
                    if (remaining == 0) break;
                }
                if (remaining != 0) return null;
            }

            // Reserve all protected effect groups before generating even the first unlocked slot.
            var excluded = ids.Where((_, slot) => fixedLocks[slot] || oneTime[slot]).ToList();
            var kept = original.Clone();
            (kept.Option1Lock, kept.Option2Lock, kept.Option3Lock) = (fixedLocks[0], fixedLocks[1], fixedLocks[2]);
            kept.IsOption1DisposableLock = kept.IsOption2DisposableLock = kept.IsOption3DisposableLock = false;
            var candidate = kept.Clone();
            for (int slot = 0; slot < ids.Length; slot++)
            {
                if (fixedLocks[slot] || oneTime[slot]) continue;
                ids[slot] = rollUnlocked(slot, ids[slot], excluded);
                if (ids[slot] != 0) excluded.Add(ids[slot]);
            }
            (candidate.Option1Id, candidate.Option2Id, candidate.Option3Id) = (ids[0], ids[1], ids[2]);
            pending = new EquipmentAwakeningData { Isn = isn, Option = candidate, IsNewData = true };

            var inventory = user.Items.ToList();
            try
            {
                foreach (var debit in debits)
                    EquipmentUtils.DeductMaterials(debit.Item, debit.Amount, user, items);
                current.Option = kept;
                user.EquipmentAwakenings.Add(pending);
                JsonDb.Save();
            }
            catch
            {
                foreach (var debit in debits) debit.Item.Count = debit.Before;
                user.Items = inventory;
                current.Option = original;
                user.EquipmentAwakenings.Remove(pending);
                throw;
            }
            return candidate.Clone();
        }
    }

    private static Dictionary<int, int> GetCosts(int oldFixed, int fixedCount, int disposableCount)
    {
        const int lockCostGroup = 100, rollCostGroup = 200;
        var totals = new Dictionary<int, int>();
        for (int level = oldFixed; level < fixedCount; level++)
            AddCost(Rate(lockCostGroup, level, 0).CostId);
        for (int level = 0; level < disposableCount; level++)
            AddCost(Rate(lockCostGroup, fixedCount, level).DisposableFixCostId);
        AddCost(Rate(rollCostGroup, fixedCount + disposableCount, 0).CostId);
        return totals;

        static EquipmentOptionCostRecord Rate(int group, int fixedLevel, int disposableLevel) =>
            GameData.Instance.EquipmentOptionCostTable.Values.Single(x =>
                x.CostGroupId == group && x.CostLevel == fixedLevel && x.DisposableFixCostLevel == disposableLevel);

        void AddCost(int costId)
        {
            foreach (var entry in GameData.Instance.costTable[costId].Costs.Where(x => x.ItemValue > 0))
            {
                if (entry.ItemType != RewardType.Item || entry.ItemId <= 0)
                    throw new InvalidDataException($"Unsupported equipment option cost {costId}");
                totals[entry.ItemId] = checked(totals.GetValueOrDefault(entry.ItemId) + entry.ItemValue);
            }
        }
    }
}
