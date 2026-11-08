using EpinelPS.Data;
using EpinelPS.LobbyServer.Character;
using EpinelPS.LobbyServer.Character.Counsel;
using EpinelPS.LobbyServer.Gacha;
using EpinelPS.Utils;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    private static void CounselAndTrainDaily(User user, DateTime now)
    {
        var counselNames = GameData.Instance.AttractiveCounselTable.Values.Select(c => c.NameCode).ToHashSet();
        var owned = user.Characters.OrderByDescending(c => user.RepresentationTeamDataNew.Contains(c.Csn))
            .ThenBy(c => c.Csn).Select(c => GameData.Instance.CharacterTable[c.Tid].NameCode).Where(counselNames.Contains).Distinct().ToArray();
        int budget = user.ResetableData.GetAvailableCounselCount(), counseled = 0;
        foreach (int name in owned.Take(Math.Max(0, budget)))
        {
            QuickCounsel.ApplyQuickCounsel(user, new ReqCharacterQuickCounsel { NameCode = name });
            user.ResetableData.DailyCounselCount[1] = budget - ++counseled;
            user.AddTrigger(Trigger.CharacterCounsel, 1);
        }
        DailyLog(user, now, $"妮姬咨询 {counseled} 次，好感已增加，剩余 {user.ResetableData.GetAvailableCounselCount()} 次");
        var recipient = user.BondInfo.FirstOrDefault(b => owned.Contains(b.NameCode) && b.Lv < user.GetMaxAttractiveLevel(b.NameCode));
        var gift = user.Items.OrderBy(i => i.ItemType).FirstOrDefault(i => i.Count > 0 &&
            GameData.Instance.itemMaterialTable.GetValueOrDefault(i.ItemType)?.ItemSubType == ItemSubType.AttractiveMaterial);
        if (recipient != null && gift != null)
        {
            var request = new ReqCharacterPresent { NameCode = recipient.NameCode };
            request.Items.Add(new NetItemData { Tid = gift.ItemType, Count = 1 });
            Present.Give(user, request);
            DailyLog(user, now, "赠送已有好感材料 1 份");
        }
        TrainDailyCharacters(user, now);
    }

    private static void TrainDailyCharacters(User user, DateTime now)
    {
        int upgraded = 0, levels = 0, skills = 0;
        foreach (var character in user.Characters.Where(c => user.RepresentationTeamDataNew.Contains(c.Csn)).ToArray())
        {
            var definition = GameData.Instance.CharacterTable[character.Tid];
            var body = user.Items.FirstOrDefault(i => i.ItemType == definition.PieceId && i.Count > 0);
            if (body != null && GameData.Instance.CharacterTable.Values.Any(c => c.NameCode == definition.NameCode && c.GradeCoreId == definition.GradeCoreId + 1))
            {
                CharacterUpgradeService.Upgrade(user, character.Csn, body.Isn, 1);
                upgraded++;
            }
        }
        ManagedNpcGrowth.Refresh(user);
        if (user.SynchroDeviceUpgraded)
        {
            while (levels < DailyTrainingLimit && user.SynchroDeviceLevel < ManagedNpcGrowth.MaximumSynchroLevel(user) &&
                PayDailyLevel(user, user.SynchroDeviceLevel + 1))
            { user.SynchroDeviceLevel++; levels++; }
        }
        else
        {
            foreach (var character in user.Characters.OrderByDescending(c => c.Level).ThenBy(c => c.Csn).Take(5).ToArray())
            {
                var rarity = GameData.Instance.CharacterTable[character.Tid].OriginalRare;
                int cap = rarity == OriginalRareType.SSR ? character.Grade >= 3 ? 200 : 160 : rarity == OriginalRareType.SR ? 160 : 80;
                if (levels < DailyTrainingLimit && character.Level < cap && PayDailyLevel(user, character.Level + 1))
                { character.Level++; levels++; }
            }
            ManagedNpcGrowth.Refresh(user);
        }
        if (levels > 0)
        {
            user.AddTrigger(Trigger.CharacterLevelUpCount, levels);
            user.AddTrigger(Trigger.CharacterLevelMax, user.GetSynchroLevel());
            user.ManagedNpc!.LevelsToday += levels;
        }
        foreach (var character in user.Characters.Where(c => user.RepresentationTeamDataNew.Contains(c.Csn)).OrderBy(c => c.Skill1Lvl))
        {
            if (character.Skill1Lvl >= 10) continue;
            try { SkillLevelUpHelper.Upgrade(user, character.Csn, CharacterSkillCategory.Skill1); skills++; }
            catch (BadHttpRequestException ex) when (ex.Message.StartsWith("Insufficient", StringComparison.Ordinal)) { continue; }
            if (skills >= 3) break;
        }
        ManagedNpcRoster.Refresh(user);
        DailyLog(user, now, $"实际养成：升级 {levels} 次、突破 {upgraded} 次、技能 {skills} 次；均已扣除材料");
    }

    private static bool PayDailyLevel(User user, int level)
    {
        if (!GameData.Instance.GetCharacterLevelUpData().TryGetValue(level, out var cost)) return false;
        if (!user.CanSubtractCurrency(CurrencyType.Gold, cost.Gold) || !user.CanSubtractCurrency(CurrencyType.CharacterExp, cost.CharacterExp) ||
            !user.CanSubtractCurrency(CurrencyType.CharacterExp2, cost.CharacterExp2)) return false;
        user.SubtractCurrency(CurrencyType.Gold, cost.Gold);
        user.SubtractCurrency(CurrencyType.CharacterExp, cost.CharacterExp);
        user.SubtractCurrency(CurrencyType.CharacterExp2, cost.CharacterExp2);
        return true;
    }

    private static void RecruitDaily(User user, DateTime now)
    {
        if (!DailyUnlocked(user, ContentsOpen.Summon)) { DailyLog(user, now, "招募：尚未解锁"); return; }
        int banner = 1, count;
        CurrencyType currency;
        long tickets = user.GetCurrencyVal(CurrencyType.CharPremiumTicket);
        if (tickets > 0) { currency = CurrencyType.CharPremiumTicket; count = tickets >= 10 ? 10 : 1; }
        else if (DailyUnlocked(user, ContentsOpen.FriendPointGacha) && user.GetCurrencyVal(CurrencyType.FriendshipPoint) >= 10)
        { currency = CurrencyType.FriendshipPoint; banner = 2; count = user.GetCurrencyVal(currency) >= 100 ? 10 : 1; }
        else if (user.GetCurrencyVal(CurrencyType.FreeCash) >= 300) { currency = CurrencyType.FreeCash; count = 1; }
        else { DailyLog(user, now, "招募：招募券、友情点数或免费钻石不足"); return; }
        if (!GameData.Instance.gachaTypes.ContainsKey(banner)) { DailyLog(user, now, "招募：当前数据没有可用卡池"); return; }
        ExecGacha.Execute(user, new ReqExecuteGacha { Tid = banner, Count = count, CurrencyType = (int)currency });
        ManagedNpcGrowth.Refresh(user);
        ManagedNpcRoster.Refresh(user);
        DailyLog(user, now, $"完成实际招募 {count} 次，支付 {currency}；角色和里程已入账");
    }
}
