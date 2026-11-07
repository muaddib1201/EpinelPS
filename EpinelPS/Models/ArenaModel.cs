namespace EpinelPS.Models;

public sealed class ArenaWorld
{
    public DateTime LastUtc { get; set; }
    public DateTime NextDayUtc { get; set; }
    public int Season { get; set; }
}

public sealed class ArenaPlayer
{
    public DateTime? RookieJoinedUtc { get; set; }
    public DateTime? SpecialJoinedUtc { get; set; }
    public int Rating { get; set; } = 1000;
    public long SpecialRank { get; set; }
    public int Day { get; set; }
    public int RookieCount { get; set; }
    public int SpecialCount { get; set; }
    public long RookieRefresh { get; set; }
    public long SpecialRefresh { get; set; }
    public DateTime AccruedUtc { get; set; }
    public long BoxTicks { get; set; }
    public decimal ChipScaled { get; set; }
    public decimal PointScaled { get; set; }
    public ArenaBattle? Pending { get; set; }
    public List<ArenaReceipt> Receipts { get; set; } = [];
    // Compact identities survive response-cache expiry, so an old packet can never pay a new battle.
    public Dictionary<string, string> PacketHashes { get; set; } = [];
    public List<byte[]> RookieRecords { get; set; } = [];
    public List<byte[]> SpecialRecords { get; set; } = [];
    public List<byte[]> RewardHistory { get; set; } = [];
    public long RewardSequence { get; set; }
    public List<TriggerModelNew> PendingTriggers { get; set; } = [];
    public long LastTriggerAt { get; set; }
}

public sealed class ArenaBattle
{
    public string MatchId { get; set; } = "";
    public bool Special { get; set; }
    public ulong EnemyId { get; set; }
    public int Season { get; set; }
    public DateTime StartedUtc { get; set; }
    public int Nonce { get; set; }
    public byte[] Enemy { get; set; } = [];
    public List<int> AttackTeams { get; set; } = [];
}

public sealed class ArenaReceipt
{
    public string Key { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public byte[] Response { get; set; } = [];
}
