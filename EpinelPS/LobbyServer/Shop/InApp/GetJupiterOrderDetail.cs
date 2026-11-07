namespace EpinelPS.LobbyServer.Shop.InApp;

[GameRequest("/inappshop/jupiter/getorderdetail")]
public class GetJupiterOrderDetail : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqGetJupiterOrderDetail req = await ReadData<ReqGetJupiterOrderDetail>();
        User user = GetUser();

        // simulated purchases are paid as soon as they are created
        string? cost = InAppPurchaseHelper.ConfirmPendingOrder(user.ID, req.ReferenceId);

        await WriteDataAsync(new ResGetJupiterOrderDetail
        {
            // the client only treats "PAY_SUCCESS" as paid
            Status = cost != null ? "PAY_SUCCESS" : "PAY_FAIL",
            ReferenceId = req.ReferenceId,
            TransactionId = req.ReferenceId,
            OriginAmount = cost ?? string.Empty,
            PayAmount = cost ?? string.Empty,
            PreferentialAmount = "0",
            CurrencyType = "USD",
            OrderInfo = "{}",
        });
    }
}
