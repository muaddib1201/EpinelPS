using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.LobbyUser;

[GameRequest("/User/Get")]
public class GetUser : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqGetUserData req = await ReadData<ReqGetUserData>();
        ResGetUserData response = new();
        User user = GetUser();

        TimeSpan battleTime = DateTime.UtcNow - user.BattleTime;
        long battleTimeMs = (long)(battleTime.TotalNanoseconds / 100);


        var now = (ctx!.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        response.User = LobbyHandler.CreateNetUserDataFromUser(user, now);
        response.Gsn = GuildDirectory.Membership(user)?.Gsn ?? 0;
        response.ResetHour = JsonDb.Instance.ResetHourUtcTime;
        response.OutpostBattleTime = new NetOutpostBattleTime() { MaxBattleTime = 864000000000, MaxOverBattleTime = 12096000000000, BattleTime = battleTimeMs };
        response.OutpostBattleLevel = user.OutpostBattleLevel;
        response.IsSimple = req.IsSimple;

        foreach (KeyValuePair<CurrencyType, long> item in user.Currency)
        {
            response.Currency.Add(new NetUserCurrencyData() { Type = (int)item.Key, Value = item.Value });
        }
        response.RepresentationTeam = NetUtils.GetDisplayedTeam(user);

        response.LastClearedNormalMainStageId = user.LastNormalStageCleared;
        response.LastClearedStoryStageId = user.LastStoryStageCleared;
        response.LastClearedHardMainStageId = user.LastHardStageCleared;
        response.LastClearedMod = user.LastClearedDifficulty;

        response.CommanderRoomJukeboxBgm = new NetJukeboxBgm() { JukeboxTableId = user.CommanderMusic.TableId, Type = NetJukeboxBgmType.JukeboxTableId, Location = NetJukeboxLocation.CommanderRoom };
        response.LobbyJukeboxBgm = new NetJukeboxBgm() { JukeboxTableId = user.LobbyMusic.TableId, Type = NetJukeboxBgmType.JukeboxTableId, Location = NetJukeboxLocation.Lobby };

        await WriteDataAsync(response);
    }
}
