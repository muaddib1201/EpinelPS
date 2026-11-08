using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.FavoriteItem;

[GameRequest("/favoriteitem/compose")]
public class ComposeCollectionItem : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var request = await ReadData<ReqComposeCollectionItem>();
        var user = GetUser();
        ResComposeCollectionItem response;
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!ReferenceEquals(JsonDb.GetUser(user.ID), user))
                throw new BadHttpRequestException("Stale collection inventory.", 409);
            response = Compose(user, request.FavoriteItemId.ToArray());
        }
        await WriteDataAsync(response);
    }

    private static ResComposeCollectionItem Compose(User user, long[] ids)
    {
        var selected = ids.ToHashSet();
        if (ids.Length == 0 || selected.Count != ids.Length || ids.Any(id => id <= 0))
            throw new BadHttpRequestException("Select distinct collection instances.", 400);

        var data = GameData.Instance;
        var inventory = user.FavoriteItems.ToLookup(item => item.FavoriteItemId);
        FavoriteItemRare rarity = FavoriteItemRare.None;
        foreach (long id in ids)
        {
            var matches = inventory[id];
            if (matches.Count() != 1)
                throw new BadHttpRequestException("Collection instance not found or ambiguous.", 400);
            var item = matches.Single();
            if (item.Csn != 0 || item.Lv != 0 || item.Exp != 0 ||
                !data.FavoriteItemTable.TryGetValue(item.Tid, out var definition) ||
                definition.FavoriteType != FavoriteItemType.Collection ||
                definition.FavoriteRare is not (FavoriteItemRare.R or FavoriteItemRare.SR))
                throw new BadHttpRequestException("Only unused R or SR collections can be composed.", 400);
            if (rarity != FavoriteItemRare.None && rarity != definition.FavoriteRare)
                throw new BadHttpRequestException("Collections must have the same rarity.", 400);
            rarity = definition.FavoriteRare;
        }

        var (need, pool) = ReadRule(data, rarity);
        if (ids.Length % need != 0)
            throw new BadHttpRequestException("Select complete groups of collections.", 400);

        // Resolve and validate every payout before removing any instance. Do not use
        // UseLootBox here: it saves partway through a batch and MergeRewards omits collections.
        var payouts = new Dictionary<(RewardType Type, int Id), int>();
        long totalWeight = pool.Sum(row => (long)row.Ratio);
        for (int group = 0; group < ids.Length / need; group++)
        {
            long roll = Random.Shared.NextInt64(totalWeight);
            var result = pool.First(row => (roll -= row.Ratio) < 0);
            int count = (int)Random.Shared.NextInt64(result.RewardValueMin, (long)result.RewardValueMax + 1);
            var key = (result.RewardType, result.RewardId);
            payouts[key] = checked(payouts.GetValueOrDefault(key) + count);
        }
        foreach (var (key, count) in payouts.Where(p => p.Key.Type == RewardType.Item))
        {
            var stack = user.Items.FirstOrDefault(i => i.ItemType == key.Id && i.Level == 0 && i.Corp == 0);
            if (stack != null) _ = checked(stack.Count + count);
        }

        var favoritesBefore = user.FavoriteItems.ToArray();
        var itemsBefore = user.Items.Select(item => (Item: item, item.Count)).ToArray();
        var usedIds = favoritesBefore.Select(item => item.FavoriteItemId).Concat(user.Items.Select(item => item.Isn)).ToHashSet();
        var reward = new NetRewardData();
        try
        {
            user.FavoriteItems.RemoveAll(item => selected.Contains(item.FavoriteItemId));
            foreach (var (key, count) in payouts)
            {
                if (key.Type == RewardType.Item)
                {
                    RewardUtils.AddSingleObject(user, ref reward, key.Id, key.Type, count);
                    continue;
                }
                for (int index = 0; index < count; index++)
                {
                    long id;
                    do { id = Random.Shared.Next(1, int.MaxValue); } while (!usedIds.Add(id));
                    var item = new NetUserFavoriteItemData { FavoriteItemId = id, Tid = key.Id };
                    user.FavoriteItems.Add(item);
                    reward.UserFavoriteItems.Add(item.Clone());
                    reward.FavoriteItems.Add(new NetFavoriteItemData { FavoriteItemId = id, Tid = key.Id });
                }
            }
            JsonDb.Save();
        }
        catch
        {
            // Preserve the original lists and objects for other requests waiting on this user.
            user.FavoriteItems.Clear(); user.FavoriteItems.AddRange(favoritesBefore);
            user.Items.Clear();
            foreach (var (item, count) in itemsBefore) { item.Count = count; user.Items.Add(item); }
            throw;
        }
        return new ResComposeCollectionItem { Reward = reward };
    }

    private static (int Need, ItemRandomRecord[] Pool) ReadRule(GameData data, FavoriteItemRare rarity)
    {
        string prefix = "FavoriteItem" + rarity + "Compose";
        if (!data.GameSettings.TryGetValue(prefix + "NeedCount", out var needSetting) ||
            !int.TryParse(needSetting.Value, out int need) || need <= 0 ||
            !data.GameSettings.TryGetValue(prefix + "Reward", out var rewardSetting) ||
            !int.TryParse(rewardSetting.Value, out int rewardId))
            throw new InvalidDataException("Collection composition configuration is missing or invalid.");

        var reward = data.GetRewardTableEntry(rewardId);
        if (reward == null || reward.UserExp != 0 || reward.CharacterExp != 0 || reward.Rewards == null || reward.Rewards.Length != 1)
            throw new InvalidDataException("Invalid collection composition reward.");
        var boxReward = reward.Rewards[0];
        if (boxReward.RewardPercent != 1000000 || boxReward.RewardType != RewardType.ItemRandomBox || boxReward.RewardValue != 1 ||
            !data.ConsumableItems.TryGetValue(boxReward.RewardId, out var box) ||
            box.UseType != ItemUseType.ItemRandomBox || box.ItemSubType != ItemSubType.ItemRandomBoxList)
            throw new InvalidDataException("Invalid collection composition reward box.");

        var pool = data.RandomItem.Values.Where(row => row.GroupId == box.UseId).ToArray();
        if (pool.Length == 0 || pool.Any(row => row.Ratio < 0 || row.RewardValueMin <= 0 || row.RewardValueMax < row.RewardValueMin ||
            (row.RewardType == RewardType.Item
                ? !data.itemMaterialTable.ContainsKey(row.RewardId) && !data.ConsumableItems.ContainsKey(row.RewardId)
                : row.RewardType != RewardType.FavoriteItem || !data.FavoriteItemTable.TryGetValue(row.RewardId, out var item) ||
                  item.FavoriteType != FavoriteItemType.Collection || item.FavoriteRare != FavoriteItemRare.SR)) ||
            pool.Sum(row => (long)row.Ratio) <= 0)
            throw new InvalidDataException("Invalid collection composition probability pool.");
        return (need, pool);
    }
}
