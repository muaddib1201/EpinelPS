namespace EpinelPS.LobbyServer.Shop.InApp;

[GameRequest("/inappshop/getreceivableproductlist")]
public class GetRetrivableProductList : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqGetInAppShopReceivableProductList x = await ReadData<ReqGetInAppShopReceivableProductList>();
        User user = GetUser();

        ResGetInAppShopReceivableProductList response = new();
        response.DataList.AddRange(InAppPurchaseHelper.GetReceivableProducts(user.ID));

        await WriteDataAsync(response);
    }
}
