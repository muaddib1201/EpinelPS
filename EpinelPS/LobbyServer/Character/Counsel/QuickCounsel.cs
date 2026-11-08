using EpinelPS.Data;
using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Character.Counsel;

[GameRequest("/character/counsel/quick")]
public class QuickCounsel : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqCharacterQuickCounsel req = await ReadData<ReqCharacterQuickCounsel>();
        User user = GetUser();

        ResCharacterQuickCounsel response;
        lock (user)
        lock (JsonDb.SaveLock)
        {
            response = CounselMissionDelivery.Apply(user, req, ctx?.Items["Nonce"] as byte[], () => ApplyQuickCounsel(user, req));
        }
        await WriteDataAsync(response);
    }

    internal static ResCharacterQuickCounsel ApplyQuickCounsel(User user, ReqCharacterQuickCounsel req)
    {
        ResCharacterQuickCounsel response = new();

        foreach (KeyValuePair<CurrencyType, long> currency in user.Currency)
        {
            response.Currencies.Add(new NetUserCurrencyData() { Type = (int)currency.Key, Value = currency.Value });
        }

        NetUserAttractiveData? bondInfo = user.BondInfo.FirstOrDefault(x => x.NameCode == req.NameCode);

        if (bondInfo != null)
        {
            int beforeLv = bondInfo.Lv;
            int beforeExp = bondInfo.Exp;

            bondInfo.CounseledCount++;
            bondInfo.CanCounselToday = true; // Always allow counseling
            if (bondInfo.Lv < user.GetMaxAttractiveLevel(req.NameCode))
            {
                bondInfo.Exp += 100;
                UpdateAttractiveLevel(bondInfo);
            }

            response.Attractive = bondInfo.Clone();
            response.Exp = new NetIncreaseExpData
            {
                NameCode = bondInfo.NameCode,
                BeforeLv = beforeLv,
                BeforeExp = beforeExp,
                CurrentLv = bondInfo.Lv,
                CurrentExp = bondInfo.Exp,
                GainExp = 100
            };
        }
        else
        {
            NetUserAttractiveData data = new()
            {
                NameCode = req.NameCode,
                Exp = 100,
                CounseledCount = 1,
                IsFavorites = false,
                CanCounselToday = true,
                Lv = 1
            };
            UpdateAttractiveLevel(data);
            user.BondInfo.Add(data);
            response.Attractive = data.Clone();
            response.Exp = new NetIncreaseExpData
            {
                NameCode = data.NameCode,
                BeforeLv = 1,
                BeforeExp = 0,
                CurrentLv = 1,
                CurrentExp = 100,
                GainExp = 100
            };
        }

        return response;
    }

    private static void UpdateAttractiveLevel(NetUserAttractiveData attractiveData)
    {
        while (attractiveData.Lv < 40)
        {
            AttractiveLevelRecord? levelInfo = GameData.Instance.AttractiveLevelTable.Values.FirstOrDefault(x => x.AttractiveLevel == attractiveData.Lv);

            if (levelInfo == null)
            {
                // No more level data
                break;
            }

            if (attractiveData.Exp >= levelInfo.AttractivePoint)
            {
                attractiveData.Exp -= levelInfo.AttractivePoint;
                attractiveData.Lv++;
            }
            else
            {
                break;
            }
        }
    }
}
