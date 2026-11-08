using EpinelPS.Data;
using EpinelPS.LobbyServer.Mission;

namespace EpinelPS.LobbyServer.Character;

[GameRequest("/character/skill/levelup")]
public class SkillLevelUp : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqCharacterSkillLevelUp req = await ReadData<ReqCharacterSkillLevelUp>();
        User user = GetUser();
        ResCharacterSkillLevelUp response = new();

        SkillLevelUpResult result = MissionProgressDelivery.Apply(user,
            () => SkillLevelUpHelper.Upgrade(user, req.Csn, (CharacterSkillCategory)req.Category));
        response.Character = result.Character;
        response.Items.AddRange(result.Items);
        response.Currencies.AddRange(result.Currencies);

        await WriteDataAsync(response);
    }
}
