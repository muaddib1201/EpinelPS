using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Soloraid;

[GameRequest("/soloraid/getranking")]
public class GetRanking : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        await ReadData<ReqGetSoloRaidRanking>();
        var user = GetUser();

        ResGetSoloRaidRanking response = new();

        try
        {
            SoloRaidHelper.GetSoloRaidRanking(user, ref response);
        }
        catch (InvalidDataException ex)
        {
            Logging.Warn(ex.Message);
            ctx!.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        await WriteDataAsync(response);
    }
}
