using EpinelPS.Data;

namespace EpinelPS.LobbyServer.LobbyUser;

[GameRequest("/User/GetProfileFrame")]
public class GetProfileFrame : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqGetProfileFrame req = await ReadData<ReqGetProfileFrame>();
        ResGetProfileFrame response = new();
        var user = GetUser();
        var rankingFrames = GameData.Instance.TriggerTable.Values.Where(t => t.HandlerType == HandlerType.Ranking)
            .Select(t => GameData.Instance.RewardDataRecords.GetValueOrDefault(t.RewardId))
            .OfType<RewardRecord>().SelectMany(r => r.Rewards).Where(r => r.RewardType == RewardType.Frame)
            .Select(r => r.RewardId).ToHashSet();
        HashSet<int> owned;
        lock (user) owned = user.FrameList.Append(user.ProfileFrame).ToHashSet();

        foreach (var frameRecord in GameData.Instance.userFrameTable.Values)
        {
            // Preserve the legacy non-ranking catalogue and the equipped frame.
            // Ranking frames are owned only after their reward mail is claimed.
            if (!rankingFrames.Contains(frameRecord.Id) || owned.Contains(frameRecord.Id))
                response.Frames.Add(frameRecord.Id);
        }

        await WriteDataAsync(response);
    }
}
