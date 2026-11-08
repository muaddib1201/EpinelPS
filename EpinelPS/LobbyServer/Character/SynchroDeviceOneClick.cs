using EpinelPS.Data;
using EpinelPS.LobbyServer.Mission;

namespace EpinelPS.LobbyServer.Character;

[GameRequest("/character/synchrodevice/deviceoneclick")]
public class SynchroDeviceOneClick : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqSynchroDeviceOneClick req = await ReadData<ReqSynchroDeviceOneClick>();
        User user = GetUser();
        var response = MissionProgressDelivery.Apply(user, () => Upgrade(user, req.SynchroLv));
        await WriteDataAsync(response);
    }

    private static ResSynchroDeviceOneClick Upgrade(User user, int targetLv)
    {
        ResSynchroDeviceOneClick response = new();

        if (targetLv > user.SynchroDeviceLevel)
        {
            var levelData = GameData.Instance.GetCharacterLevelUpData();

            int totalGold = 0;
            int totalExp = 0;
            int totalExp2 = 0;

            int lv;
            for (lv = user.SynchroDeviceLevel; lv < targetLv; lv++)
            {
                if (levelData.TryGetValue(lv + 1, out var data))
                {
                    totalGold += data.Gold;
                    totalExp += data.CharacterExp;
                    totalExp2 += data.CharacterExp2;
                }
                else { break; }
            }

            if (lv > user.SynchroDeviceLevel &&
                user.CanSubtractCurrency(CurrencyType.Gold, totalGold) &&
                user.CanSubtractCurrency(CurrencyType.CharacterExp, totalExp) &&
                user.CanSubtractCurrency(CurrencyType.CharacterExp2, totalExp2))
            {
                user.SubtractCurrency(CurrencyType.Gold, totalGold);
                user.SubtractCurrency(CurrencyType.CharacterExp, totalExp);
                user.SubtractCurrency(CurrencyType.CharacterExp2, totalExp2);
                user.SynchroDeviceLevel = lv;
                user.SynchroDeviceUpgraded = true;
                user.AddTrigger(Trigger.CharacterLevelUpCount, 1);
            }
        }

        response.SynchroLv = user.SynchroDeviceLevel;
        foreach (var c in user.Currency)
            response.Currencies.Add(new NetUserCurrencyData() { Type = (int)c.Key, Value = c.Value });

        return response;
    }
}
