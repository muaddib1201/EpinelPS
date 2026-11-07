namespace EpinelPS.Models;

public class InterceptionProgress
{
    public Dictionary<string, InterceptionBest> BestByBoss { get; set; } = [];
    public List<InterceptionReceipt> Receipts { get; set; } = [];
}

public class InterceptionBest
{
    public int Day { get; set; }
    public long Damage { get; set; }
}

public class InterceptionReceipt
{
    public string? Key { get; set; }
    public long CreatedAt { get; set; }
    public byte[] Response { get; set; } = [];
    // An outbox commits mission progress with the paid clear, across the JSON/SQLite stores.
    public List<TriggerModelNew> PendingTriggers { get; set; } = [];
}
