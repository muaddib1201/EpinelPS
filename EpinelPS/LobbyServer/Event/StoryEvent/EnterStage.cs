namespace EpinelPS.LobbyServer.Event.StoryEvent;

[GameRequest("/event/storydungeon/enterstage")]
public class EnterEventStoryStage : StoryDungeonMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqEnterEventStage>();
        var key = BattleKey("enter", req.StageId, req.TeamNumber, req.AntiCheatAdditionalInfo?.ClientLocalTime);
        await Reply(req.EventId, story => story.Enter(req.StageId, req.TeamNumber, key));
    }
}
