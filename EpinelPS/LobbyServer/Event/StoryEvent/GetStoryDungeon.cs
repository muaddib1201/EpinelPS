using EpinelPS.Data;

namespace EpinelPS.LobbyServer.Event.StoryEvent;

[GameRequest("/event/storydungeon/get")]
public class GetStoryDungeon : StoryDungeonMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqStoryDungeonEventData>();
        await Reply(req.EventId, story =>
        {
            var state = story.State;
            var response = new ResStoryDungeonEventData
            {
                RemainTicket = story.Balance,
                TeamData = story.User.UserTeams.GetValueOrDefault((int)TeamType.StoryEvent)?.Clone()
                    ?? new NetUserTeamData { Type = (int)TeamType.StoryEvent }
            };
            foreach (var stageId in state.ClearedStages)
                response.LastClearedEventStageList.Add(new NetLastClearedEventStageData { StageId = stageId });
            response.LastClearedEventStageList.Add(new NetLastClearedEventStageData { StageId = state.LastStage });
            return response;
        });
    }
}
