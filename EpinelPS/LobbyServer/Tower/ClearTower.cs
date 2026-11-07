using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Team;
using EpinelPS.Utils;
using Org.BouncyCastle.Ocsp;

namespace EpinelPS.LobbyServer.Tower;

[GameRequest("/tower/cleartower")]
public class ClearTower : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqClearTower req = await ReadData<ReqClearTower>();

        ResClearTower response = new();
        User user = GetUser();
        user.ResetDataIfNeeded();

        if (req.BattleResult == 1) response.Reward = TowerHelper.CompleteTower(user, req.TowerId);

        SupportCharacterHelper.FinishBattle(user, SupportCharacterHelper.GetTowerTeamType(req.TowerId), req.TowerId, req.TeamNumber, req.BattleResult);

        JsonDb.Save();
        await WriteDataAsync(response);
    }
}
