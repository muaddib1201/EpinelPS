namespace EpinelPS.LobbyServer.Event.StoryEvent;

[GameRequest("/event/storydungeon/clearstage")]
public class ClearEventStage : StoryDungeonMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqClearEventStage>();
        var key = BattleKey("clear", req.StageId, req.TeamNumber, req.AntiCheatAdditionalInfo?.ClientLocalTime);
        await Reply(req.EventId, story => ClearEventStageHelper.Clear(story, req, key));
    }
}
