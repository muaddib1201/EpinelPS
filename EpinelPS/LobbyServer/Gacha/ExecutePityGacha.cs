using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Gacha;

//
// This endpoint was adapted from ExecGacha
//

[GameRequest("/gacha/pity/execute")]
public class ExecutePityGacha : LobbyMessage
{
    protected override async Task HandleAsync()
    {

        ReqExecuteGachaPity req = await ReadData<ReqExecuteGachaPity>();

        ResExecuteGachaPity response = new ResExecuteGachaPity();
        response.Reward = new NetRewardData();

        // Add the pity count
        User user = GetUser();
        user.AddPityExecuteCount(req.GachaPityId);


        // Obtain the character
        List<CharacterRecord> selectedCharacters = new List<CharacterRecord>();
        selectedCharacters.Add(GameData.Instance.CharacterTable[req.CharacterId]);

        foreach (CharacterRecord characterData in selectedCharacters)
            response.Reward.MergeFrom(CharacterRewardHelper.GrantCharacter(user, characterData, 1));

        // The pity request can unlock a room through the newly obtained
        // character, so re-evaluate eligible openers after character handling.
        MessengerMessageCreator.CreateAllEligibleOpeners(user);

        var pityBanner = GameData.Instance.GachaPityRecords.First().Value;
        response.GachaPityProgress = new NetGachaPityData()
        {
            GachaPityId = req.GachaPityId,
            PityExecuteCount = user.GachaPityBannerExecuteCount.Where(b => b.Key == req.GachaPityId).Sum(b => b.Value),
            TotalGachaCount = user.GetGachaCountForType(GachaPremiumType.GachaPremium),
            TotalUsedGachaCount = user.GachaPityBannerExecuteCount.Where(b => b.Key == req.GachaPityId).Sum(b => b.Value) * pityBanner.NeedGachaCount
        };



        JsonDb.Save();

        await WriteDataAsync(response);
    }
}
