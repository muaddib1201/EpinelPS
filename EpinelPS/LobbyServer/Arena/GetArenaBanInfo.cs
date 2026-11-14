using Google.Protobuf.WellKnownTypes;

namespace EpinelPS.LobbyServer.Arena;

[GameRequest("/arena/getbaninfo")]
public class GetArenaBanInfo : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqGetArenaBanInfo req = await ReadData<ReqGetArenaBanInfo>();

        ResGetArenaBanInfo response = new()
        {
            ChampionArenaBanInfo = ChampionArenaAvailability.BanInfo()
        };

        await WriteDataAsync(response);
    }
}
