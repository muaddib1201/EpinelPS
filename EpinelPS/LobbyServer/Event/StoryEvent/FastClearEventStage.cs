namespace EpinelPS.LobbyServer.Event.StoryEvent;

[GameRequest("/event/storydungeon/fastclearstage")]
public class FastClearEventStage : StoryDungeonMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqFastClearEventStage>();
        var key = PacketKey("sweep");
        await Reply(req.EventId, story =>
        {
            var settled = ClearEventStageHelper.Settle(story, req.StageId, req.ClearCount, won: true, quick: true, key);
            return new ResFastClearEventStage { RemainTicket = settled.RemainTicket, Reward = settled.Reward, BonusReward = settled.BonusReward };
        });
    }
}
