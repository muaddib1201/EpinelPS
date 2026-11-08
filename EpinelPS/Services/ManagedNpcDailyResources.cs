using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Friend;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.LobbyServer.Outpost.Dispatch;
using EpinelPS.LobbyServer.Shop;
using EpinelPS.Utils;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    private static bool DailyUnlocked(User user, ContentsOpen content) =>
        GameData.Instance.ContentsOpenTable.TryGetValue(content, out var row) &&
        (row.OpenCondition ?? []).All(c => DailyCondition(user, c.OpenConditionType, c.OpenConditionValue));

    private static bool DailyCondition(User user, ContentsOpenCondition condition, int value) => condition switch
    {
        ContentsOpenCondition.None => true,
        ContentsOpenCondition.UserLevel => user.userPointData.UserLevel >= value,
        ContentsOpenCondition.StageClear => user.IsStageCompleted(value),
        ContentsOpenCondition.MainQuest => user.MainQuestData.ContainsKey(value),
        ContentsOpenCondition.GuildLevel => JsonDb.Instance.Guilds.GetValueOrDefault(user.Guild.guildId ?? 0)?.Data.Grade >= value,
        ContentsOpenCondition.MaxGachaCount => user.GachaBannerMaxPulls.Values.Sum() >= value,
        ContentsOpenCondition.MaxGachaPremiumCount => user.GetGachaCountForType(GachaPremiumType.GachaPremium) >= value,
        ContentsOpenCondition.PlayTutorialGacha => user.GetGachaCountForType(GachaPremiumType.GachaTutorial) >= value,
        ContentsOpenCondition.SimulationRoomClear => user.ResetableData.SimRoomData.CurrentDifficulty >= value,
        ContentsOpenCondition.SynchroDeviceAmplification => user.SynchroDeviceUpgraded,
        _ => false
    };

    private static long DailyCombat(User user) => user.RepresentationTeamDataNew.Sum(c => (long)FormulaUtils.CalculateCP(user, c));
    private static void DailyLog(User user, DateTime now, string text) => Log(user.ManagedNpc!, now, text);

    private static void CollectDailyResources(User user, DateTime now)
    {
        if (DailyUnlocked(user, ContentsOpen.OutpostBattle) && GameData.Instance.OutpostBattle.ContainsKey(user.OutpostBattleLevel.Level))
        {
            double minutes = Math.Clamp((now - user.BattleTime).TotalMinutes, 0, 24 * 60);
            if (minutes > 0)
            {
                NetUtils.GetOutpostReward(user, TimeSpan.FromMinutes(minutes), true);
                user.BattleTime = now;
                user.AddTrigger(Trigger.OutpostBattleReward, 1);
            }
            if (user.ResetableData.WipeoutCount == 0)
            {
                NetUtils.GetOutpostReward(user, TimeSpan.FromHours(2), true);
                user.ResetableData.WipeoutCount++;
                user.AddTrigger(Trigger.OutpostFastBattleReward, 1);
            }
            DailyLog(user, now, $"前哨收益已入账（累计 {minutes:F0} 分钟），每日免费歼灭已核对");
        }
        else DailyLog(user, now, "前哨收益：尚未解锁");

        if (DailyUnlocked(user, ContentsOpen.Friend))
        {
            var gifts = FriendshipService.SettleNpcDaily(user, now);
            DailyLog(user, now, $"好友赠点 {gifts.Sent}，领取 {gifts.Received}");
        }
        if (DailyUnlocked(user, ContentsOpen.DispatchBoard)) SettleDailyDispatch(user, now);
        if (DailyUnlocked(user, ContentsOpen.Shop)) PurchaseDailyShop(user, now);
        if (DailyUnlocked(user, ContentsOpen.Mail))
        {
            int claimed = 0;
            foreach (var mail in user.MailDatas.Values.Where(m => m.State == 1).ToArray())
            {
                NetRewardData reward = new();
                if (mail.HasReward)
                    foreach (var item in mail.Items.Where(i => i.ExpiredAt >= now.Ticks))
                        RewardUtils.AddSingleObject(user, ref reward, item.RewardId, (RewardType)item.RewardType, item.RewardValue);
                mail.State = 2;
                claimed++;
            }
            DailyLog(user, now, $"领取可用邮件 {claimed} 封");
        }
    }

    private static void PurchaseDailyShop(User user, DateTime now)
    {
        var shop = NormalShopHelper.GetShopData(user, (int)ShopCategoryType.ShopNormal);
        foreach (var row in shop.List.OrderBy(r => GameData.Instance.ContentsShopProductTable.GetValueOrDefault(r.ProductId)?.PriceValue ?? int.MaxValue))
        {
            var product = GameData.Instance.ContentsShopProductTable.GetValueOrDefault(row.ProductId);
            if (product == null || product.PriceValue > 0 && (product.PriceType != PriceType.Currency || product.PriceId != (int)CurrencyType.Gold)) continue;
            var response = BuyProduct.Purchase(user, new ReqShopBuyProduct
                { ShopCategory = (int)ShopCategoryType.ShopNormal, ShopProductTid = row.ProductId, Order = row.Order, Quantity = 1 });
            if (response.Result == ShopBuyProductResult.Expired) continue;
            DailyLog(user, now, $"普通商店购买 {row.ProductId}，实际支付 {product.PriceValue}");
            return;
        }
        DailyLog(user, now, "普通商店：没有可购买的免费或信用点商品");
    }

    private static void SettleDailyDispatch(User user, DateTime now)
    {
        var rows = user.UserDispatchData.dispatchDatas ??= [];
        int claimed = 0;
        foreach (var running in rows.Where(r => r.IsRun == 1 && r.EndAt > 0 && r.EndAt <= now.Ticks).ToArray())
        {
            if (!GameData.Instance.DispatchTable.TryGetValue(running.Tid, out var definition)) continue;
            RewardUtils.RegisterRewardsForUser(user, definition.RewardId);
            user.AddUnique(user.DispatchClearList, running.Tid);
            rows.Remove(running);
            user.AddTrigger(Trigger.SendDispatch, 1);
            user.AddTrigger(Trigger.SendDispatchGrade, 1, definition.DispatchGradeId);
            claimed++;
        }
        DispatchHelper.SyncDispatchLevel(user);
        int day = GuildActivity.Day(now);
        if (user.UserDispatchData.Today != day)
        {
            user.UserDispatchData.Today = day;
            rows.RemoveAll(r => r.IsRun != 1);
            user.DispatchClearList.Clear();
            user.SelectableDispatchData.RemoveAll(r => r.EndAt <= now.Ticks);
            var board = GameData.Instance.DispatchBoardTable.Values.FirstOrDefault(b => b.DispatchType == DispatchType.Dispatch && b.DispatchBoardLv == user.DispatchLv);
            if (board != null)
            {
                for (int i = rows.Count; i < Math.Max(0, board.DispatchMax); i++)
                {
                    var groups = board.DispatchList.Where(g => GameData.Instance.DispatchTable.Values.Any(d => d.DispatchGroup == g.DispatchGroup &&
                        rows.All(r => r.Tid != d.Id) && CanDailyDispatch(user, d))).ToList();
                    if (groups.Count == 0) break;
                    var group = DispatchHelper.SelectItemByProbability(groups, g => g.DispatchProb);
                    if (group == null) break;
                    var available = GameData.Instance.DispatchTable.Values.Where(d => d.DispatchGroup == group.DispatchGroup &&
                        rows.All(r => r.Tid != d.Id) && CanDailyDispatch(user, d)).OrderBy(d => d.Id).ToArray();
                    var chosen = available[new Random(unchecked(user.ManagedNpc!.Seed ^ day ^ i)).Next(available.Length)];
                    rows.Add(new NetUserDispatchData { Tid = chosen.Id });
                }
            }
        }
        int started = 0;
        foreach (var row in rows.Where(r => r.IsRun != 1).ToArray())
        {
            if (!GameData.Instance.DispatchTable.TryGetValue(row.Tid, out var definition) || !CanDailyDispatch(user, definition)) continue;
            row.IsRun = 1; row.StartAt = now.Ticks; row.EndAt = now.AddMinutes(Math.Max(0, definition.TimeMin)).Ticks;
            user.SelectableDispatchData.RemoveAll(r => r.SelectTid == row.Tid);
            user.SelectableDispatchData.Add(new NetSelectableDispatchData { SelectTid = row.Tid, DispatchGroupId = definition.DispatchGradeId,
                SelectSlotId = started + 1, IsRun = true, StartAt = row.StartAt, EndAt = row.EndAt });
            started++;
        }
        DailyLog(user, now, $"派遣：领取 {claimed} 项，开始 {started} 项；未到结束时间的奖励继续等待");
    }

    private static bool CanDailyDispatch(User user, DispatchRecord dispatch)
    {
        var available = user.Characters.Select(c => GameData.Instance.CharacterTable[c.Tid]).ToList();
        if (available.Count < dispatch.SlotMax) return false;
        foreach (var requirement in dispatch.DispatchSlotArray ?? [])
        {
            var match = available.FirstOrDefault(c => (requirement.Rare == 0 || c.OriginalRare == requirement.Rare) &&
                (requirement.DispatchSlotType is DispatchSlotType.None or DispatchSlotType.ALL ||
                 requirement.DispatchSlotType == DispatchSlotType.CHARACTER && c.NameCode == requirement.SlotValue ||
                 requirement.DispatchSlotType != DispatchSlotType.CHARACTER && (int)c.Corporation == (int)requirement.DispatchSlotType));
            if (match == null) return false;
            available.Remove(match);
        }
        return true;
    }
}
