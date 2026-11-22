namespace EpinelPS.LobbyServer.Inventory;

[GameRequest("/inventory/equipment/getawakeningdetail")]
public class GetAwakeningDetail : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqGetAwakeningDetail>();
        var user = GetUser();
        var response = new ResGetAwakeningDetail();
        lock (user)
        {
            var options = user.EquipmentAwakenings.Where(x => x.Isn == req.Isn);
            response.CurrentOption = options.FirstOrDefault(x => !x.IsNewData)?.Option.Clone();
            response.NewOption = options.FirstOrDefault(x => x.IsNewData)?.Option.Clone();
        }
        await WriteDataAsync(response);
    }
}
