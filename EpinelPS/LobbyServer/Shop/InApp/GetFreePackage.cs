using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Shop.InApp;

[GameRequest("/inappshop/getfreepackage")]
public class GetFreePackage : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqGetFreePackage req = await ReadData<ReqGetFreePackage>();
        User user = GetUser();
        Logging.WriteLine($"[InAppShop] Free package request: type {req.ProductType}, shop {req.PackageShopTid}, list {req.PackageListTid}", LogType.Info);

        if (!InAppPurchaseHelper.TryClaimFreePackage(user, req, out NetRewardData reward))
        {
            Logging.WriteLine($"[InAppShop] Free package {req.PackageShopTid}/{req.PackageListTid} not claimable for user {user.ID}", LogType.Warning);
            reward = new NetRewardData { IsEmptyReward = true, PassPoint = new NetPassPointData() };
        }

        await WriteDataAsync(new ResGetFreePackage { Reward = reward });
    }
}
