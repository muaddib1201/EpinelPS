using EpinelPS.LobbyServer.Mission;

namespace EpinelPS.LobbyServer.Outpost.Dispatch;

[GameRequest("/Outpost/ResetDispatchList")]
public class ResetDispatchList : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        await ReadData<ReqResetDispatchList>();
        User user = GetUser();
        var response = MissionProgressDelivery.Apply(user, () => DispatchHelper.ResetBoard(user));
        await WriteDataAsync(response);
    }
}
