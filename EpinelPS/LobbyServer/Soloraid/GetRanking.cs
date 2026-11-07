namespace EpinelPS.LobbyServer.Soloraid;

[GameRequest("/soloraid/getranking")]
public class GetRanking : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        await ReadData<ReqGetSoloRaidRanking>();
        var user = GetUser();

        ResGetSoloRaidRanking response = new();

        SoloRaidHelper.GetSoloRaidRanking(user, ref response);

        await WriteDataAsync(response);
    }
}
