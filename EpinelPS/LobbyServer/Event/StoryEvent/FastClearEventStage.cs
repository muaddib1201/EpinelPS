namespace EpinelPS.LobbyServer.Event.StoryEvent;

[GameRequest("/event/storydungeon/fastclearstage")]
public class FastClearEventStage : StoryDungeonMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqFastClearEventStage>();
        var key = PacketKey("sweep");
        await Reply(req.EventId, story => ClearEventStageHelper.FastClear(story, req, key));
    }
}
