using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Shop;

[GameRequest("/shop/buy")]
public class BuyProduct : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqShopBuyProduct req = await ReadData<ReqShopBuyProduct>();
        User user = GetUser();

        var response = MissionProgressDelivery.Apply(user, () => Purchase(user, req));
        await WriteDataAsync(response);
    }

    private ResShopBuyProduct Purchase(User user, ReqShopBuyProduct req)
    {

        ResShopBuyProduct response = new()
        {
            Product = new NetShopBuyProductData(),
        };

        // The client omits quantity for a normal single purchase, which is
        // decoded by protobuf as 0. A single-buy request still means one item.
        int quantity = req.Quantity > 0 ? req.Quantity : 1;
        var request = new NormalShopHelper.PurchaseRequest(req.ShopProductTid, req.Order, quantity);
        if (!NormalShopHelper.TryPurchase(user, req.ShopCategory, [request], out var purchased,
                out var currencies, out var costItems))
        {
            response.Result = ShopBuyProductResult.Expired;
            return response;
        }

        response.Currencies.AddRange(currencies);
        response.Item = costItems.FirstOrDefault();
        var purchase = purchased[0];
        response.Product.BuyCount = purchase.BuyCount;
        GrantProduct(user, ref response, purchase.Product, purchase.Quantity);
        if (req.ShopCategory == (int)ShopCategoryType.ShopNormal)
            user.AddTrigger(Trigger.MainShopBuy, purchase.Quantity);
        return response;
    }

    private void GrantProduct(User user, ref ResShopBuyProduct response, ContentsShopProductRecord product, int quantity)
    {
        Console.WriteLine($"Bought: {product.GoodsType}");
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
