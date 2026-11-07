using EpinelPS.LobbyServer.Mission;

namespace EpinelPS.LobbyServer.Character;

[GameRequest("/character/coreupgrade")]
public class CoreUpgrade : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqCharacterCoreUpgrade>();
        var user = GetUser();
        var upgraded = MissionProgressDelivery.Apply(user,
            () => CharacterUpgradeService.Upgrade(user, req.Csn, req.Isn, req.Count));
        await WriteDataAsync(new ResCharacterCoreUpgrade { Character = upgraded.Character, Items = { upgraded.Items } });
    }
}