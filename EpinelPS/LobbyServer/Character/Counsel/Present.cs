using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Character.Counsel;

[GameRequest("/character/attractive/present")]
public class Present : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqCharacterPresent req = await ReadData<ReqCharacterPresent>();
        User user = GetUser();

        var response = MissionProgressDelivery.Apply(user, () => Give(user, req));
        await WriteDataAsync(response);
    }

    internal static ResCharacterPresent Give(User user, ReqCharacterPresent req)
    {

        ResCharacterPresent response = new();

        NetUserAttractiveData? bondInfo = user.BondInfo.FirstOrDefault(x => x.NameCode == req.NameCode);
        if (bondInfo == null)
        {
            throw new BadHttpRequestException("Unknown gift recipient.", 400);
        }

        int totalExpGained = 0;
        CharacterRecord? characterRecord = GameData.Instance.CharacterTable.Values.FirstOrDefault(x => x.NameCode == req.NameCode);
        if (characterRecord == null || !user.Characters.Any(c => GameData.Instance.CharacterTable.TryGetValue(c.Tid, out var owned) && owned.NameCode == req.NameCode))
            throw new BadHttpRequestException("Gift recipient is not owned.", 400);
        if (req.Items.Count == 0 || req.Items.Any(i => i.Count <= 0))
            throw new BadHttpRequestException("Gift quantities must be positive.", 400);
        var gifts = req.Items.GroupBy(i => i.Tid).Select(g => new { Tid = g.Key, Count = checked(g.Sum(i => i.Count)) }).ToArray();
        foreach (var item in gifts)
        {
            if (!GameData.Instance.itemMaterialTable.TryGetValue(item.Tid, out var material) || material.ItemSubType != ItemSubType.AttractiveMaterial ||
                user.Items.Where(i => i.ItemType == item.Tid && i.Count > 0).Sum(i => (long)i.Count) < item.Count)
                throw new BadHttpRequestException("Gift material or quantity is unavailable.", 400);
        }

        foreach (var item in gifts)
        {
            ItemMaterialRecord? materialInfo = GameData.Instance.itemMaterialTable.GetValueOrDefault(item.Tid);
            if (materialInfo != null && materialInfo.ItemSubType == ItemSubType.AttractiveMaterial)
            {
                int expGained = checked(materialInfo.ItemValue * item.Count);

                if (characterRecord != null)
                {
                    if (materialInfo.MaterialType == MaterialType.Corporation)
                    {
                        string? corporation = materialInfo.NameLocalkey?.Split('_').ElementAtOrDefault(2);
                        if (string.Equals(corporation, characterRecord.Corporation.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            expGained = checked(expGained * 5);
                        }
                    }
                    else if (materialInfo.MaterialType == MaterialType.Squad)
                    {
                        string? squad = materialInfo.NameLocalkey?.Split('_').ElementAtOrDefault(2);
                        if (string.Equals(squad, characterRecord.Squad.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            expGained = checked(expGained * 3);
                        }
                    }
                }

                totalExpGained = checked(totalExpGained + expGained);

                int remaining = item.Count;
                foreach (var userItem in user.Items.Where(x => x.ItemType == item.Tid && x.Count > 0).ToArray())
                {
                    int consumed = Math.Min(userItem.Count, remaining);
                    userItem.Count -= consumed;
                    remaining -= consumed;
                    if (userItem.Count == 0) user.Items.Remove(userItem);
                    if (remaining == 0) break;
                }
            }
        }

        int beforeLv = bondInfo.Lv;
        int beforeExp = bondInfo.Exp;

        if (bondInfo.Lv < user.GetMaxAttractiveLevel(req.NameCode))
        {
            bondInfo.Exp = checked(bondInfo.Exp + totalExpGained);
            UpdateAttractiveLevel(bondInfo);
        }

        // Outpost events can require a character's attractive level to be maxed
        if (bondInfo.Lv >= user.GetMaxAttractiveLevel(req.NameCode) &&
            !MissionTriggerHistory.Read(user).Any(t => t.Type == Trigger.CharacterAttractiveLevelMax && t.ConditionId == req.NameCode))
        {
            user.AddTrigger(Trigger.CharacterAttractiveLevelMax, bondInfo.Lv, req.NameCode);
        }

        response.Attractive = bondInfo;
        response.Exp = new NetIncreaseExpData
        {
            NameCode = bondInfo.NameCode,
            BeforeLv = beforeLv,
            BeforeExp = beforeExp,
            CurrentLv = bondInfo.Lv,
            CurrentExp = bondInfo.Exp,
            GainExp = totalExpGained
        };

        response.Items.AddRange(NetUtils.GetUserItems(user));

        user.AddTrigger(Trigger.CharacterAttractivePresent, checked(gifts.Sum(i => i.Count)));
        return response;
    }

    private static void UpdateAttractiveLevel(NetUserAttractiveData attractiveData)
    {
        while (attractiveData.Lv < 40)
        {
            AttractiveLevelRecord? levelInfo = GameData.Instance.AttractiveLevelTable.Values.FirstOrDefault(x => x.AttractiveLevel == attractiveData.Lv);

            if (levelInfo == null)
            {
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
