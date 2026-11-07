using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Character;

[GameRequest("/character/attractive/obtainreward")]
public class ObtainEpReward : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqObtainAttractiveReward req = await ReadData<ReqObtainAttractiveReward>();
        ResObtainAttractiveReward response = new();
        User user = GetUser();

        var episode = GameData.Instance.AttractiveLevelReward.Values.FirstOrDefault(row =>
            row.NameCode == req.NameCode && row.AttractiveLevel == req.Lv);
        lock (user)
        lock (JsonDb.SaveLock)
        {
            var bond = user.BondInfo.FirstOrDefault(item => item.NameCode == req.NameCode);
            if (episode != null && bond != null && bond.Lv >= req.Lv && !bond.ObtainedRewardLevels.Contains(req.Lv))
            {
                response.Reward = RewardUtils.RegisterRewardsForUser(user, episode.RewardId);
                bond.ObtainedRewardLevels.Add(req.Lv);
                JsonDb.Save();
            }
        }

        await WriteDataAsync(response);
    }
}
