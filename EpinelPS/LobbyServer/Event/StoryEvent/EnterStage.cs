namespace EpinelPS.LobbyServer.Event.StoryEvent;

[GameRequest("/event/storydungeon/enterstage")]
public class EnterEventStoryStage : StoryDungeonMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqEnterEventStage>();
        await Reply(req.EventId, story =>
        {
            var (battle, _) = story.Resolve(req.StageId);
            story.RequireTickets(battle, 1);
            return new ResEnterEventStage();
        });
    }
}
