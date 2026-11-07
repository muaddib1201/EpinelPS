using EpinelPS.Data;
using EpinelPS.LobbyServer.Mission;

namespace EpinelPS.LobbyServer.Simroom;

[GameRequest("/simroom/selectbuff")]
public class SelectBuff : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqSelectSimRoomBuff req = await ReadData<ReqSelectSimRoomBuff>();
        User user = GetUser();
        var response = MissionProgressDelivery.Apply(user, () => Settle(user, req));
        await WriteDataAsync(response);
    }

    private static ResSelectSimRoomBuff Settle(User user, ReqSelectSimRoomBuff req)
    {
        ResSelectSimRoomBuff response = new()
        {
            Result = SimRoomResult.Success
        };

        var location = req.Location;
        if (location == null) throw new BadHttpRequestException("Simulation event location is missing.", 400);
        var sim = user.ResetableData.SimRoomData;
        var events = sim.Events;
        var battle = events.FirstOrDefault(e => e.Location.Chapter == location.Chapter && e.Location.Stage == location.Stage && e.Location.Order == location.Order);
        if (battle == null || battle.EventCase != (int)NetSimRoomEvent.EventOneofCase.Battle || battle.Battle.Id != req.Event)
            throw new BadHttpRequestException("Unknown simulation battle.", 400);
        // Replaying a successful selection cannot add the buff, rewards or clear twice.
        if (battle.Battle.Progress == (int)SimRoomBattleEventProgress.RewardReceived) return response;
        if (battle.Battle.Progress != (int)SimRoomBattleEventProgress.RewardWaiting)
            throw new BadHttpRequestException("Simulation battle has not been completed.", 400);
        if (req.BuffToAdd > 0 && !battle.Battle.BuffOptions.Contains(req.BuffToAdd))
            throw new BadHttpRequestException("Invalid simulation buff selection.", 400);

        if (req.BuffToDelete > 0) sim.Buffs.Remove(req.BuffToDelete);
        if (req.BuffToAdd > 0 && !sim.Buffs.Contains(req.BuffToAdd)) sim.Buffs.Add(req.BuffToAdd);
        battle.Selected = true;
        battle.Battle.Progress = (int)SimRoomBattleEventProgress.RewardReceived;

        var chapter = GameData.Instance.SimulationRoomChapterTable.Values.FirstOrDefault(c => c.DifficultyId == sim.CurrentDifficulty && c.Chapter == location.Chapter);
        if (chapter != null && location.Stage == chapter.StageValue &&
            GameData.Instance.SimulationRoomBattleEventTable.TryGetValue(battle.Battle.Id, out var definition) && definition.EventType == SimulationRoomEvent.BossBattle)
        {
            var reward = SimRoomHelper.SimRoomReceivedReward(user, sim.CurrentDifficulty, location.Chapter);
            if (reward != null) response.Reward = reward;
            var overclockReward = SimRoomHelper.SimRoomOverclockReceivedReward(user);
            if (overclockReward != null) response.RewardByOverclock = overclockReward;
            SimRoomHelper.UpdateOverclockHighScoreData(user, location);
            SimRoomHelper.RecordClear(user);
        }
        return response;
    }




}
