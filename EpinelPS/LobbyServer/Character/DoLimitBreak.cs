using EpinelPS.LobbyServer.Mission;

namespace EpinelPS.LobbyServer.Character;

[GameRequest("/character/upgrade")]
public class DoLimitBreak : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqCharacterUpgrade>();
        var user = GetUser();
        var response = MissionProgressDelivery.Apply(user,
            () => CharacterUpgradeService.Upgrade(user, req.Csn, req.Isn, req.Count));
        await WriteDataAsync(response);
    }
}