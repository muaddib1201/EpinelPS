namespace EpinelPS.LobbyServer.Event.StoryEvent;

[GameRequest("/event/storydungeon/clearstage")]
public class ClearEventStage : StoryDungeonMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqClearEventStage>();
        var time = req.AntiCheatAdditionalInfo?.ClientLocalTime;
        var key = time > 0 ? $"battle/{req.StageId}/{req.TeamNumber}/{time}" : PacketKey("clear");
        await Reply(req.EventId, story => ClearEventStageHelper.Settle(story, req.StageId, 1, req.BattleResult == 1, quick: false, key));
    }
}
