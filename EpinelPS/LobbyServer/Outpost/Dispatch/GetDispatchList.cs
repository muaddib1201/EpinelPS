using EpinelPS.LobbyServer.Mission;

namespace EpinelPS.LobbyServer.Outpost.Dispatch;

[GameRequest("/outpost/dispatch/get")]
public class GetDispatchList : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        await ReadData<ReqGetDispatchList>();
        User user = GetUser();
        var response = MissionProgressDelivery.Apply(user, () =>
        {
            DispatchHelper.PrepareBoard(user, DateTime.UtcNow);
            var result = new ResGetDispatchList { DispatchResetCount = user.DispatchResetCount };
            result.DispatchList.AddRange(user.UserDispatchData.dispatchDatas.Select(r => r.Clone()));
            return result;
        });
        await WriteDataAsync(response);
    }
}
