namespace EpinelPS.Models;

public sealed class TriggerClockSyncReceipt
{
    public string PacketKey { get; set; } = "";
    public byte[] Response { get; set; } = [];
}

public sealed class CounselMissionReceipt
{
    public string? PacketKey { get; set; }
    public long CreatedAt { get; set; }
    public byte[] Response { get; set; } = [];
    public bool PendingTrigger { get; set; }
}
