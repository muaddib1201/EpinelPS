namespace EpinelPS.Models;

public class TextAdventurePlusData
{
    public MiniGameTextAdventurePlusPlayData PlayData { get; set; } = new();
    public List<int> ReceivedProgressRewards { get; set; } = [];
    public List<int> ReceivedEndingRewards { get; set; } = [];
    // Finish has no request ID. Keep receipts until the next explicit route entry.
    public int ActiveRouteId { get; set; }
    public string? LastFinishSignature { get; set; }
    public HashSet<string> EarlierFinishSignatures { get; set; } = [];
    public bool ShouldSerializeEarlierFinishSignatures() => EarlierFinishSignatures.Count != 0;
}
