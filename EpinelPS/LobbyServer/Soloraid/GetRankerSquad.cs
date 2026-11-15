namespace EpinelPS.LobbyServer.Soloraid;

[GameRequest("/soloraid/getrankersquad")]
public class GetRankerSquad : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqGetSoloRaidRankerSquad>();
        var response = SoloRaidHelper.GetRankerSquad(GetUser(), req.TargetUsn, req.RaidId);
        await WriteDataAsync(response);
    }
}
