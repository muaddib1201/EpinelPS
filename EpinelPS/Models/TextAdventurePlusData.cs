namespace EpinelPS.Models;

public class TextAdventurePlusData
{
    public MiniGameTextAdventurePlusPlayData PlayData { get; set; } = new();
    public List<int> ReceivedProgressRewards { get; set; } = [];
    public List<int> ReceivedEndingRewards { get; set; } = [];
    // Track the route and last settlement because Finish has no request ID.
    public int ActiveRouteId { get; set; }
    public string? LastFinishSignature { get; set; }
}
