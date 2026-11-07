namespace EpinelPS.LobbyServer.Shop.InApp;

[GameRequest("/inappshop/startpurchase")]
public class StartPurchase : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqStartPurchase req = await ReadData<ReqStartPurchase>();
        User user = GetUser();
        bool success = InAppPurchaseHelper.TrySimulatePurchase(user, req.ProductId, req.ExtraData, out _, out string referenceId);

        var response = new ResStartPurchase
        {
            Result = success ? StartPurchaseResult.Ok : StartPurchaseResult.PayChannelNotAvailable,
            TransactionId = referenceId,
        };
        await WriteDataAsync(response);
    }
}
