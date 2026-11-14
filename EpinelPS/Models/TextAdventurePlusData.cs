namespace EpinelPS.Models;

public class TextAdventurePlusData
{
    public MiniGameTextAdventurePlusPlayData PlayData { get; set; } = new();
    public List<int> ReceivedProgressRewards { get; set; } = [];
    public List<int> ReceivedEndingRewards { get; set; } = [];
    // Finish has no request ID. A route entry arms exactly one settlement.
    public int ActiveRouteId { get; set; }
}
