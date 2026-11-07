namespace EpinelPS.LobbyServer.Event;

[GameRequest("/event/boxgacha/get")]
public class GetEventBoxGacha : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        // from client: {"EventId":10051}
        ReqGetEventBoxGacha req = await ReadData<ReqGetEventBoxGacha>();
        User user = GetUser();

        ResGetEventBoxGacha response = new();

        if (user.EventBoxGachaInfo.TryGetValue(req.EventId, out EventBoxGachaData? data))
        {
            response.GachaCount = data.GachaCount;
            response.RewardOrders.AddRange(data.RewardOrders);
        }

        await WriteDataAsync(response);
    }
}
