using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Shop;

[GameRequest("/shop/multiple-buy")]
public class BuyMultipleProduct : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqShopBuyMultipleProduct req = await ReadData<ReqShopBuyMultipleProduct>();
        User user = GetUser();

        var response = MissionProgressDelivery.Apply(user, () => Purchase(user, req));
        await WriteDataAsync(response);
    }

    private ResShopBuyMultipleProduct Purchase(User user, ReqShopBuyMultipleProduct req)
    {

        ResShopBuyMultipleProduct response = new()
        {
            Product = new NetShopBuyMultipleProductData(),
        };

        var requests = req.Products
            .Select(x => new NormalShopHelper.PurchaseRequest(x.ShopProductTid, x.Order, x.Quantity > 0 ? x.Quantity : 1))
            .ToList();
        if (!NormalShopHelper.TryPurchase(user, req.ShopCategory, requests, out var purchased,
                out var currencies, out var costItems))
        {
            response.Result = ShopBuyProductResult.Expired;
            return response;
        }

        response.Currencies.AddRange(currencies);
        response.Items.AddRange(costItems);
        foreach (var purchase in purchased)
        {
            response.Product.BuyCounts.Add(new NetBuyCountData
            {
                Order = purchase.Order,
                BuyCount = purchase.BuyCount,
            });
            GrantProduct(user, ref response, purchase.Product, purchase.Quantity);
        }

        if (req.ShopCategory == (int)ShopCategoryType.ShopNormal)
            user.AddTrigger(Trigger.MainShopBuy, purchased.Sum(p => p.Quantity));
        return response;
    }

    private void GrantProduct(User user, ref ResShopBuyMultipleProduct response, ContentsShopProductRecord product, int quantity)
    {
        int totalValue = checked(product.GoodsValue * quantity);
        NetRewardData characterReward = new();
        if (product.GoodsType == RewardType.Character ||
            (product.GoodsType == RewardType.Item && CharacterRewardHelper.TryGrantSpareBody(user, product.GoodsId, totalValue, out characterReward)))
        {
            if (product.GoodsType == RewardType.Character)
                RewardUtils.AddSingleObject(user, ref characterReward, product.GoodsId, RewardType.Character, totalValue);
            response.Product.Character.AddRange(characterReward.Character);
            response.Product.UserCharacters.AddRange(characterReward.UserCharacters);
            response.Product.Item.AddRange(characterReward.Item);
            response.Product.UserItems.AddRange(characterReward.UserItems);
            response.Product.Currency.AddRange(characterReward.Currency);
            foreach (var currency in characterReward.Currency)
                response.Currencies.Add(new NetUserCurrencyData { Type = currency.Type, Value = currency.FinalValue });
            return;
        }

        if (product.GoodsType == RewardType.Currency)
        {
            user.AddCurrency((CurrencyType)product.GoodsId, totalValue);
            response.Product.Currency.Add(new NetCurrencyData
            {
                Type = product.GoodsId,
                Value = totalValue,
                FinalValue = user.GetCurrencyVal((CurrencyType)product.GoodsId),
            });
        }
        else if (product.GoodsType == RewardType.Item || product.GoodsType.ToString().StartsWith("Equipment"))
        {
            var existing = user.Items.FirstOrDefault(i => i.ItemType == product.GoodsId);
            if (existing != null)
            {
                existing.Count += totalValue;
                response.Product.Item.Add(NetUtils.ItemDataToNet(existing, totalValue));
                response.Product.UserItems.Add(NetUtils.UserItemDataToNet(existing));
            }
            else
            {
                var newItem = new DbItemData
                {
                    ItemType = product.GoodsId,
                    Count = totalValue,
                    Isn = user.GenerateUniqueItemId(),
                };
                user.Items.Add(newItem);
                response.Product.Item.Add(NetUtils.ItemDataToNet(newItem, totalValue));
                response.Product.UserItems.Add(NetUtils.UserItemDataToNet(newItem));
            }
        }
        else if (product.GoodsType == RewardType.LiveWallpaper)
        {
            if (!user.LiveWallpaperList.Contains(product.GoodsId))
                user.LiveWallpaperList = [.. user.LiveWallpaperList, product.GoodsId];
            response.Product.LiveWallPapers.Add(product.GoodsId);
        }
        else if (product.GoodsType == RewardType.UserTitle)
        {
            if (!user.TitleList.Contains(product.GoodsId))
                user.TitleList.Add(product.GoodsId);
            response.Product.UserTitleList.Add(product.GoodsId);
        }
    }
}
