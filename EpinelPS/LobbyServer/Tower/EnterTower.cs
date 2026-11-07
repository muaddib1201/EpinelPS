using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.LobbyServer.Team;
using Google.Protobuf;
using System.Security.Cryptography;

namespace EpinelPS.LobbyServer.Tower;

[GameRequest("/tower/entertower")]
public class EnterTower : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqEnterTower req = await ReadData<ReqEnterTower>();
        User user = GetUser();

        var nonce = ctx?.Items["Nonce"] as byte[];
        var response = MissionProgressDelivery.Apply(user, () =>
        {
            string? key = nonce is { Length: > 0 } ? Convert.ToHexString(SHA256.HashData(nonce)) : null;
            string body = Convert.ToHexString(SHA256.HashData(req.ToByteArray()));
            if (key != null && user.TowerEntryReceipts.TryGetValue(key, out var previous))
            {
                if (previous != body) throw new BadHttpRequestException("Tower entry nonce was reused for another request.", 409);
                return new ResEnterTower();
            }
            SupportCharacterHelper.BeginBattle(user, SupportCharacterHelper.GetTowerTeamType(req.TowerId), req.TowerId, req.TeamNumber);
            user.AddTrigger(Trigger.TowerAllStart, 1);
            if (key != null) user.TowerEntryReceipts[key] = body;
            return new ResEnterTower();
        });
        await WriteDataAsync(response);
    }
}
