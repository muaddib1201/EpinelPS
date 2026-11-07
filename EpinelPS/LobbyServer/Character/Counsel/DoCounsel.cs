using EpinelPS.Data;
using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Character.Counsel;

[GameRequest("/character/attractive/counsel")]
public class DoCounsel : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqCharacterCounsel req = await ReadData<ReqCharacterCounsel>();
        User user = GetUser();
        int gainExp = await GetAffinityGainAsync(req);

        ResCharacterCounsel response;
        lock (user)
        lock (JsonDb.SaveLock)
        {
            response = CounselMissionDelivery.Apply(user, req, ctx?.Items["Nonce"] as byte[], () => ApplyCounsel(user, req, gainExp));
        }
        await WriteDataAsync(response);
    }

    private static async Task<int> GetAffinityGainAsync(ReqCharacterCounsel req)
    {
        var gameData = GameData.Instance;
        if (string.IsNullOrEmpty(req.SelectTid) ||
            !gameData.AttractiveCounselTable.TryGetValue(req.CounselTid, out var counsel) ||
            counsel.NameCode != req.NameCode || string.IsNullOrEmpty(counsel.CounselGroupId))
            return 100;

        var dialogs = await gameData.LoadZip<AttractiveCounselDialogRecord>(
            $"AttractiveCounselDialogTable.{counsel.CounselGroupId}.json", new Progress<double>());
        var answer = dialogs.FirstOrDefault(row => row.Id == req.SelectTid);
        // Unrecognized or non-scoring rows retain the legacy baseline, never the bonus.
        return answer?.AttractivePoint == AttractiveCounselDialogRecordAttractive_point._120 ? 120 : 100;
    }

    private ResCharacterCounsel ApplyCounsel(User user, ReqCharacterCounsel req, int gainExp)
    {
        ResCharacterCounsel response = new();

        foreach (KeyValuePair<CurrencyType, long> currency in user.Currency)
        {
            response.Currencies.Add(new NetUserCurrencyData() { Type = (int)currency.Key, Value = currency.Value });
        }

        NetUserAttractiveData? currentBondInfo = user.BondInfo.FirstOrDefault(x => x.NameCode == req.NameCode);

        if (currentBondInfo != null)
        {
            int beforeLv = currentBondInfo.Lv;
            int beforeExp = currentBondInfo.Exp;

            currentBondInfo.CounseledCount++;
            if (!currentBondInfo.CounselDialogCompleteIds.Contains(req.CounselTid))
                currentBondInfo.CounselDialogCompleteIds.Add(req.CounselTid);
            currentBondInfo.CanCounselToday = true; // Always allow counseling
            if (currentBondInfo.Lv < user.GetMaxAttractiveLevel(req.NameCode))
            {
                currentBondInfo.Exp += gainExp;
                UpdateAttractiveLevel(currentBondInfo);
            }

            // Outpost events can require a character's attractive level to be maxed
            if (currentBondInfo.Lv >= user.GetMaxAttractiveLevel(req.NameCode) &&
                !GameContext.Triggers.Any(t => t.UserId == user.ID && t.Type == Trigger.CharacterAttractiveLevelMax && t.ConditionId == req.NameCode))
            {
                user.AddTrigger(Trigger.CharacterAttractiveLevelMax, currentBondInfo.Lv, req.NameCode);
            }

            response.Attractive = currentBondInfo.Clone();
            response.Exp = new NetIncreaseExpData
            {
                NameCode = currentBondInfo.NameCode,
                BeforeLv = beforeLv,
                BeforeExp = beforeExp,
                CurrentLv = currentBondInfo.Lv,
                CurrentExp = currentBondInfo.Exp,
                GainExp = gainExp
            };
        }
        else
        {
            NetUserAttractiveData data = new()
            {
                NameCode = req.NameCode,
                Exp = gainExp,
                CounseledCount = 1,
                IsFavorites = false,
                CanCounselToday = true,
                Lv = 1
            };
            data.CounselDialogCompleteIds.Add(req.CounselTid);
            UpdateAttractiveLevel(data);
            user.BondInfo.Add(data);
            response.Attractive = data.Clone();
            response.Exp = new NetIncreaseExpData
            {
                NameCode = data.NameCode,
                BeforeLv = 1,
                BeforeExp = 0,
                CurrentLv = data.Lv,
                CurrentExp = data.Exp,
                GainExp = gainExp
            };
        }

        return response;
    }

    private void UpdateAttractiveLevel(NetUserAttractiveData attractiveData)
    {
        while (attractiveData.Lv < 40)
        {
            AttractiveLevelRecord? levelInfo = GameData.Instance.AttractiveLevelTable.FirstOrDefault(x => x.Value.AttractiveLevel == attractiveData.Lv).Value;

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
