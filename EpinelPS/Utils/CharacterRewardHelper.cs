using EpinelPS.Data;

namespace EpinelPS.Utils;

/// <summary>Character copies and spare bodies share one upgrade allowance and one compensation payment.</summary>
public static class CharacterRewardHelper
{
    public static NetRewardData GrantCharacter(User user, CharacterRecord definition, int count)
    {
        lock (user) return GrantCharacterLocked(user, definition, count);
    }

    private static NetRewardData GrantCharacterLocked(User user, CharacterRecord definition, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        var reward = new NetRewardData();
        var owned = user.GetCharacter(definition.Id);
        if (owned == null)
        {
            owned = new CharacterModel
            {
                Csn = user.GenerateUniqueCharacterId(), Tid = definition.Id,
                Grade = definition.GradeCoreId - BaseGrade(definition.OriginalRare),
                Level = 1, Skill1Lvl = 1, Skill2Lvl = 1, UltimateLevel = 1,
            };
            user.Characters.Add(owned);
            var acquired = new NetCharacterData { Csn = owned.Csn, Tid = owned.Tid };
            reward.Character.Add(acquired);
            acquired.CurrencyValue = ReconcileOwnedInventory(user, definition, reward);
            count--;
            user.AddBadge(BadgeContents.NikkeNew, definition.NameCode.ToString());
            user.AddTrigger(Trigger.ObtainCharacter, 1, definition.NameCode);
            user.AddTrigger(Trigger.ObtainCharacterNew, 1);
            if (definition.OriginalRare == OriginalRareType.SSR)
                user.AddTrigger(Trigger.ObtainCharacterSSR, 1);
            if (definition.OriginalRare != OriginalRareType.R && !user.BondInfo.Any(b => b.NameCode == definition.NameCode))
                user.BondInfo.Add(new() { NameCode = definition.NameCode, Lv = 1 });
            MessengerMessageCreator.CreateAllEligibleOpeners(user);
        }
        reward.UserCharacters.Add(ToDefault(owned));
        if (count > 0) GrantOwnedCopies(user, owned, count, reward);
        return reward;
    }

    public static bool TryGrantSpareBody(User user, int itemId, int count, out NetRewardData reward)
    {
        lock (user) return TryGrantSpareBodyLocked(user, itemId, count, out reward);
    }

    private static bool TryGrantSpareBodyLocked(User user, int itemId, int count, out NetRewardData reward)
    {
        reward = new();
        if (itemId <= 0) return false;
        var definition = GameData.Instance.CharacterTable.Values.FirstOrDefault(c => c.PieceId == itemId);
        if (definition == null || user.GetCharacter(definition.Id) is not { } owned) return false;
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        GrantOwnedCopies(user, owned, count, reward);
        return true;
    }

    private static void GrantOwnedCopies(User user, CharacterModel owned, int count, NetRewardData reward)
    {
        var definition = GameData.Instance.CharacterTable[owned.Tid];
        long legacyLabels = ReconcileOwnedInventory(user, definition, reward);
        long stored = user.Items.Where(i => i.ItemType == definition.PieceId).Sum(i => (long)Math.Max(0, i.Count));
        int spareCount = (int)Math.Min(count, Math.Max(0L, RemainingUpgrades(definition) - stored));
        if (spareCount > 0)
        {
            var item = user.Items.FirstOrDefault(i => i.ItemType == definition.PieceId);
            if (item == null)
            {
                item = new DbItemData { ItemType = definition.PieceId, Isn = user.GenerateUniqueItemId() };
                user.Items.Add(item);
            }
            item.Count = checked(item.Count + spareCount);
            reward.Item.Add(NetUtils.ItemDataToNet(item, spareCount));
            reward.UserItems.Add(NetUtils.UserItemDataToNet(item));
        }
        long labels = checked((long)(count - spareCount) * LabelValue(definition.OriginalRare));
        PayLabels(user, labels, reward);
        reward.Character.Add(new NetCharacterData
        {
            Csn = owned.Csn, Tid = owned.Tid, PieceCount = spareCount, CurrencyValue = labels + legacyLabels,
        });
    }

    /// <summary>Run before a full login inventory/currency snapshot; the remaining inventory makes this idempotent.</summary>
    public static long ConvertExistingExcess(User user)
    {
        lock (user) return ConvertExistingExcessLocked(user);
    }

    private static long ConvertExistingExcessLocked(User user)
    {
        long labels = 0;
        var processed = new HashSet<int>();
        foreach (var owned in user.Characters)
        {
            if (!GameData.Instance.CharacterTable.TryGetValue(owned.Tid, out var definition) ||
                definition.PieceId <= 0 || !processed.Add(definition.PieceId)) continue;
            labels = checked(labels + ReconcileOwnedInventory(user, definition));
        }
        return labels;
    }

    private static long ReconcileOwnedInventory(User user, CharacterRecord definition, NetRewardData? reward = null)
    {
        int keep = RemainingUpgrades(definition);
        long labels = 0;
        foreach (var item in user.Items.Where(i => i.ItemType == definition.PieceId && i.Count > 0).ToList())
        {
            int retained = Math.Min(keep, item.Count);
            keep -= retained;
            if (retained == item.Count) continue;
            labels = checked(labels + (long)(item.Count - retained) * LabelValue(definition.OriginalRare));
            item.Count = retained;
            if (item.Count == 0) user.Items.Remove(item);
            reward?.UserItems.Add(NetUtils.UserItemDataToNet(item));
        }
        PayLabels(user, labels, reward);
        return labels;
    }

    private static void PayLabels(User user, long labels, NetRewardData? reward)
    {
        if (labels <= 0) return;
        user.AddCurrency(CurrencyType.DissolutionPoint, labels);
        reward?.Currency.Add(new NetCurrencyData
        {
            Type = (int)CurrencyType.DissolutionPoint, Value = labels,
            FinalValue = user.GetCurrencyVal(CurrencyType.DissolutionPoint),
        });
    }

    private static int RemainingUpgrades(CharacterRecord character) => Math.Max(0,
        BaseGrade(character.OriginalRare) + RewardUtils.GetValueByRarity(character.OriginalRare, 0, 2, 10) - character.GradeCoreId);
    private static int BaseGrade(OriginalRareType rarity) => RewardUtils.GetValueByRarity(rarity, 201, 101, 1);
    private static int LabelValue(OriginalRareType rarity) => RewardUtils.GetValueByRarity(rarity, 150, 200, 6000);
    private static NetUserCharacterDefaultData ToDefault(CharacterModel character) => new()
    {
        Csn = character.Csn, Tid = character.Tid, Grade = character.Grade, Lv = character.Level,
        CostumeId = character.CostumeId, Skill1Lv = character.Skill1Lvl,
        Skill2Lv = character.Skill2Lvl, UltiSkillLv = character.UltimateLevel,
    };
}
