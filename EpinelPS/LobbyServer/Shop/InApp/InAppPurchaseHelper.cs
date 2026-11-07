using System.Collections.Concurrent;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Shop.InApp;

internal static class InAppPurchaseHelper
{
    // Rewards are granted on purchase, these only feed the client's reward popup.
    // The client lists them through /inappshop/getreceivableproductlist and claims them with /inappshop/getbuyproduct.
    private static readonly ConcurrentDictionary<(ulong UserId, string ProductId), PendingPurchase> PendingRewards = new();

    // Delivered: the client confirmed the order through jupiter/getorderdetail and showed the reward itself
    private record PendingPurchase(string ReferenceId, int SubTid, NetRewardData Reward, bool Delivered = false);

    // A product with a buy limit, either a package shop list entry or an event in-app (ticket) shop product
    private record ShopLimit(ProductType Type, int ShopTid, int ListTid, ShopBuyLimitType LimitType, int LimitCount);

    public static bool TrySimulatePurchase(User user, string productId, NetStartPurchaseExtraData? extraData,
        out NetRewardData reward, out string referenceId)
    {
        reward = new NetRewardData { PassPoint = new NetPassPointData() };
        referenceId = string.Empty;
        if (!GameConfig.Root.EnablePurchaseSimulation)
            return false;

        var midas = FindMidasProduct(productId);
        if (midas == null || !midas.IsActive)
            return false;

        ShopLimit? limit = midas.ProductType switch
        {
            ProductType.PackageShop => FindPackageListLimit(midas.ProductId, extraData?.PackageListTableId ?? 0),
            ProductType.EventInAppShop => FindEventShopLimit(midas.ProductId),
            _ => null,
        };
        if (limit != null && IsSoldOut(user, limit))
        {
            Logging.WriteLine($"[InAppShop] {limit.Type} {limit.ShopTid}/{limit.ListTid} is sold out for user {user.ID}", LogType.Warning);
            return false;
        }

        bool granted = midas.ProductType switch
        {
            ProductType.CashShop => GrantCashShop(user, midas.ProductId, ref reward),
            ProductType.PackageShop => GrantPackageShop(user, midas.ProductId, ref reward),
            ProductType.EventInAppShop => GrantEventInAppShop(user, midas.ProductId, ref reward),
            ProductType.CostumeShop => GrantCostumeShop(user, midas.ProductId, ref reward),
            ProductType.PassCostumeShop => GrantPassCostumeShop(user, midas.ProductId, ref reward),
            _ => false,
        };

        if (!granted)
            return false;

        if (limit != null)
            RecordBuy(user, limit);

        referenceId = $"dev-{Guid.NewGuid():N}";
        PendingRewards[(user.ID, productId)] = new PendingPurchase(referenceId, limit?.ListTid ?? 0, reward.Clone());
        JsonDb.Save();
        return true;
    }

    public static IEnumerable<NetInAppShopReceivableProductData> GetReceivableProducts(ulong userId)
    {
        foreach (var ((pendingUserId, productId), pending) in PendingRewards)
        {
            if (pendingUserId != userId || pending.Delivered)
                continue;

            yield return new NetInAppShopReceivableProductData
            {
                ProductId = productId,
                Token = pending.ReferenceId,
                SubTid = pending.SubTid,
            };
        }
    }

    // Confirms a simulated order and returns its price, null if the reference is unknown.
    // After a successful payment the client shows cash shop (gem) rewards from its refreshed wallet,
    // every other product is claimed right away through the receivable list, so only gems are marked delivered.
    public static string? ConfirmPendingOrder(ulong userId, string referenceId)
    {
        foreach (var (key, pending) in PendingRewards)
        {
            if (key.UserId == userId && pending.ReferenceId == referenceId)
            {
                var midas = FindMidasProduct(key.ProductId);
                if (midas?.ProductType == ProductType.CashShop)
                    PendingRewards[key] = pending with { Delivered = true };
                return midas?.Cost ?? "0";
            }
        }

        return null;
    }

    public static bool TryClaimFreePackage(User user, ReqGetFreePackage req, out NetRewardData reward)
    {
        reward = new NetRewardData { PassPoint = new NetPassPointData() };

        int packageGroupId;
        ShopLimit? limit;
        switch ((ProductType)req.ProductType)
        {
            case ProductType.PackageShop:
                if (!GameData.Instance.PackageShopTable.TryGetValue(req.PackageShopTid, out var package) || !package.IsFree)
                    return false;
                packageGroupId = package.PackageGroupId;
                limit = FindPackageListLimit(req.PackageShopTid, req.PackageListTid);
                break;
            case ProductType.EventInAppShop:
                // the client may send either the shop or the product id in these fields
                var product = FindEventShopProduct(req.PackageListTid) ?? FindEventShopProduct(req.PackageShopTid);
                if (product == null || !product.IsFree)
                    return false;
                packageGroupId = product.PackageGroupId;
                limit = FindEventShopLimit(product.Id);
                break;
            default:
                return false;
        }

        if (limit != null && IsSoldOut(user, limit))
            return false;

        if (!GrantPackageGroup(user, packageGroupId, ref reward))
            return false;

        if (limit != null)
            RecordBuy(user, limit);

        JsonDb.Save();
        return true;
    }

    public static IEnumerable<NetInAppShopBuyData> GetBuyDataList(User user)
    {
        foreach (var record in user.InAppShopBuyRecords)
        {
            int count = GetCurrentBuyCount(record);
            if (count == 0)
                continue;

            var limit = FindLimit(record);
            yield return new NetInAppShopBuyData
            {
                ProductType = record.ProductType,
                ShopTid = limit?.ShopTid ?? record.ShopTid,
                ListTid = limit?.ListTid ?? record.ListTid,
                BuyCount = count,
            };
        }
    }

    private static ShopLimit? FindPackageListLimit(int packageShopId, int packageListId)
    {
        PackageListRecord? packageList = null;
        if (packageListId != 0)
            GameData.Instance.PackageListTable.TryGetValue(packageListId, out packageList);

        packageList ??= GameData.Instance.PackageListTable.Values
            .Where(x => x.ProductId == packageShopId)
            .OrderByDescending(x => x.IsActive)
            .FirstOrDefault();

        return packageList == null
            ? null
            : new ShopLimit(ProductType.PackageShop, packageList.ProductId, packageList.Id,
                packageList.BuyLimitType, packageList.IsLimit ? packageList.BuyLimitCount : 0);
    }

    private static EventInAppShopProductRecord? FindEventShopProduct(int productId)
    {
        return GameData.Instance.EventInAppShopProductTable.TryGetValue(productId, out var product) ? product : null;
    }

    private static ShopLimit? FindEventShopLimit(int productId)
    {
        var product = FindEventShopProduct(productId);
        if (product == null)
            return null;

        // the client identifies event shop products by ShopTid = product id with no ListTid
        return new ShopLimit(ProductType.EventInAppShop, product.Id, 0, product.BuyLimitType, product.BuyLimitCount);
    }

    private static ShopLimit? FindLimit(InAppShopBuyRecord record)
    {
        return (ProductType)record.ProductType switch
        {
            ProductType.PackageShop => FindPackageListLimit(record.ShopTid, record.ListTid),
            // older records stored the product id in ListTid
            ProductType.EventInAppShop => FindEventShopLimit(record.ListTid != 0 ? record.ListTid : record.ShopTid),
            _ => null,
        };
    }

    private static InAppShopBuyRecord? FindRecord(User user, ShopLimit limit)
    {
        return user.InAppShopBuyRecords.FirstOrDefault(x => x.ProductType == (int)limit.Type && FindLimit(x) == limit);
    }

    private static bool IsSoldOut(User user, ShopLimit limit)
    {
        if (limit.LimitCount <= 0)
            return false;

        var record = FindRecord(user, limit);
        return record != null && GetCurrentBuyCount(record) >= limit.LimitCount;
    }

    private static void RecordBuy(User user, ShopLimit limit)
    {
        var record = FindRecord(user, limit);
        if (record == null)
        {
            record = new InAppShopBuyRecord
            {
                ProductType = (int)limit.Type,
                ShopTid = limit.ShopTid,
                ListTid = limit.ListTid,
            };
            user.InAppShopBuyRecords.Add(record);
        }

        record.BuyCount = GetCurrentBuyCount(record) + 1;
        record.LastBuyAt = DateTime.UtcNow;
    }

    // Buy count of the current daily/weekly/monthly period, 0 once the period has rolled over
    private static int GetCurrentBuyCount(InAppShopBuyRecord record)
    {
        var limit = FindLimit(record);
        if (limit == null)
            return record.BuyCount;

        DateTime now = DateTime.UtcNow;
        bool expired = limit.LimitType switch
        {
            ShopBuyLimitType.Daily or ShopBuyLimitType.Weekly or ShopBuyLimitType.Monthly =>
                GetPeriodStart(limit.LimitType, record.LastBuyAt) != GetPeriodStart(limit.LimitType, now),
            _ => false,
        };

        return expired ? 0 : record.BuyCount;
    }

    private static DateTime GetPeriodStart(ShopBuyLimitType limitType, DateTime utc)
    {
        // The game day starts at the server reset hour (20:00 UTC = 04:00 UTC+8 by default),
        // weeks start on monday and months on the 1st of that game day calendar.
        int offset = (24 - JsonDb.Instance.ResetHourUtcTime) % 24;
        DateTime gameDay = utc.AddHours(offset).Date;

        return limitType switch
        {
            ShopBuyLimitType.Daily => gameDay,
            ShopBuyLimitType.Weekly => gameDay.AddDays(-(((int)gameDay.DayOfWeek + 6) % 7)),
            ShopBuyLimitType.Monthly => new DateTime(gameDay.Year, gameDay.Month, 1),
            _ => DateTime.MinValue,
        };
    }

    public static NetRewardData TakePendingReward(ulong userId, string productId)
    {
        return PendingRewards.TryRemove((userId, productId), out var pending)
            ? pending.Reward
            : new NetRewardData { IsEmptyReward = true, PassPoint = new NetPassPointData() };
    }

    private static MidasProductRecord? FindMidasProduct(string productId)
    {
        if (GameData.Instance.mediasProductTable.TryGetValue(productId, out var exact))
            return exact;

        return GameData.Instance.mediasProductTable.Values.FirstOrDefault(x =>
            string.Equals(x.MidasProductIdProximabeta, productId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.MidasProductIdGamamobi, productId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool GrantCashShop(User user, int cashShopId, ref NetRewardData reward)
    {
        if (!GameData.Instance.CashShopRecords.TryGetValue(cashShopId, out var product) || !product.IsActive)
            return false;

        switch (product.ProductType)
        {
            case CashShopProductType.Currency:
                RewardUtils.AddSingleObject(user, ref reward, product.ProductId, RewardType.Currency, product.ProductValue);
                return true;
            case CashShopProductType.Item:
                RewardUtils.AddSingleObject(user, ref reward, product.ProductId, RewardType.Item, product.ProductValue);
                return true;
            case CashShopProductType.Package:
                return GrantPackageGroup(user, product.ProductId, ref reward);
            default:
                return false;
        }
    }

    private static bool GrantPackageShop(User user, int packageShopId, ref NetRewardData reward)
    {
        return GameData.Instance.PackageShopTable.TryGetValue(packageShopId, out var package) &&
               GrantPackageGroup(user, package.PackageGroupId, ref reward);
    }

    private static bool GrantEventInAppShop(User user, int productId, ref NetRewardData reward)
    {
        var product = FindEventShopProduct(productId);
        return product != null && GrantPackageGroup(user, product.PackageGroupId, ref reward);
    }

    private static bool GrantCostumeShop(User user, int costumeShopId, ref NetRewardData reward)
    {
        if (!GameData.Instance.CostumeShopTable.TryGetValue(costumeShopId, out var costume) || !costume.IsActive)
            return false;

        AddCostume(user, costume.CostumeId, ref reward);
        return GrantPackageGroup(user, costume.PackageGroupId, ref reward, allowEmpty: true);
    }

    private static bool GrantPassCostumeShop(User user, int passCostumeShopId, ref NetRewardData reward)
    {
        if (!GameData.Instance.PassCostumeShopTable.TryGetValue(passCostumeShopId, out var costume))
            return false;

        AddCostume(user, costume.CostumeId, ref reward);
        return GrantPackageGroup(user, costume.PackageGroupId, ref reward, allowEmpty: true);
    }

    private static bool GrantPackageGroup(User user, int packageGroupId, ref NetRewardData reward, bool allowEmpty = false)
    {
        var products = GameData.Instance.PackageGroupTable.Values
            .Where(x => x.PackageGroupId == packageGroupId)
            .ToList();
        if (products.Count == 0)
            return allowEmpty;

        foreach (var product in products)
            RewardUtils.AddSingleObject(user, ref reward, product.ProductId, product.ProductType, product.ProductValue);
        return true;
    }

    private static void AddCostume(User user, int costumeId, ref NetRewardData reward)
    {
        if (!user.CostumeList.Contains(costumeId))
            user.CostumeList.Add(costumeId);
        reward.CharacterCostume.Add(costumeId);
    }
}
