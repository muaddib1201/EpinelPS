namespace EpinelPS.LobbyServer.Shop.InApp;

[GameRequest("/inappshop/jupiter/buyproduct")]
public class BuyJupiterProduct : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqBuyJupiterProduct req = await ReadData<ReqBuyJupiterProduct>();
        User user = GetUser();
        bool success = InAppPurchaseHelper.TrySimulatePurchase(user, req.ProductId, req.ExtraData, out _, out string referenceId);

        // The PC client refuses to continue without a payment page, point it at the local fake one
        await WriteDataAsync(new ResBuyJupiterProduct
        {
            ReferenceId = referenceId,
            RedirectUrl = success ? $"http://127.0.0.1/jupiter/pay/{referenceId}" : string.Empty,
        });
    }
}
