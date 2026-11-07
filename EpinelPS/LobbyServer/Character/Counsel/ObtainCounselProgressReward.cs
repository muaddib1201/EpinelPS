using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Character.Counsel;

[GameRequest("/character/counsel/progress/reward")]
public class ObtainCounselProgressReward : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqObtainCounselProgressReward>();
        var user = GetUser();
        ResObtainCounselProgressReward response;
        lock (user)
        lock (JsonDb.SaveLock)
        {
            response = Claim(user, req.NameCode);
        }
        await WriteDataAsync(response);
    }

    private static ResObtainCounselProgressReward Claim(User user, int nameCode)
    {
        ResObtainCounselProgressReward response = new();
        var data = GameData.Instance;
        var bond = user.BondInfo.FirstOrDefault(b => b.NameCode == nameCode);
        if (bond == null || bond.CompleteRewardStatus != CounselDialogCompleteRewardStatus.NotReceived ||
            !data.AttractiveCounselCharacterTable.TryGetValue(nameCode, out var collection) || collection.CollectRewardId <= 0 ||
            !user.Characters.Any(c => data.CharacterTable.TryGetValue(c.Tid, out var character) && character.NameCode == nameCode))
            return response;

        var requiredIds = data.AttractiveCounselTable.Values.Where(row => row.NameCode == nameCode).Select(row => row.Id).ToArray();
        var completedIds = bond.CounselDialogCompleteIds.ToHashSet();
        if (requiredIds.Length == 0 || !requiredIds.All(completedIds.Contains)) return response;

        // Collection rewards are profile decorations. Validate before changing any ownership.
        var reward = data.GetRewardTableEntry(collection.CollectRewardId);
        if (reward == null || reward.UserExp != 0 || reward.CharacterExp != 0 || reward.Rewards == null || reward.Rewards.Length == 0 ||
            reward.Rewards.Any(item => item.RewardType != RewardType.ProfileCardObject || item.RewardValue <= 0 ||
                !data.ProfileCardObjectTable.TryGetValue(item.RewardId, out var card) ||
                card.ObjectType is not (ObjectType.Sticker or ObjectType.BackGround)))
            return response;

        var stickers = user.StickerList.ToArray();
        var backgrounds = user.BackgroundList.ToArray();
        var profileCards = user.ProfileCardsData.ToArray();
        try
        {
            response.Reward = RewardUtils.RegisterRewardsForUser(user, reward);
            // The profile possession endpoint reads this list after a fresh login.
            foreach (int id in response.Reward.ProfileCardObjects) user.AddUnique(user.ProfileCardsData, id);
            bond.CompleteRewardStatus = CounselDialogCompleteRewardStatus.Received;
            JsonDb.Save();
        }
        catch
        {
            user.StickerList.Clear(); user.StickerList.AddRange(stickers);
            user.BackgroundList.Clear(); user.BackgroundList.AddRange(backgrounds);
            user.ProfileCardsData.Clear(); user.ProfileCardsData.AddRange(profileCards);
            bond.CompleteRewardStatus = CounselDialogCompleteRewardStatus.NotReceived;
            throw;
        }
        return response;
    }
}
