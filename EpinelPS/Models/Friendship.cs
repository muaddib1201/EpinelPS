namespace EpinelPS.Models;

public class FriendshipState
{
    public HashSet<ulong> Friends { get; set; } = [];
    // Pair history is deliberately retained when a friendship is removed.
    public Dictionary<ulong, FriendGiftHistory> Gifts { get; set; } = [];
    public int Day { get; set; }
    public int SentCount { get; set; }
    public int ReceivedCount { get; set; }
    public bool NpcFriendsInitialized { get; set; }
    public List<FriendRequestReceipt> Receipts { get; set; } = [];
    // Reply bodies are bounded; consumed identities must survive their eviction
    // so an old encrypted packet can never consume a later day's entitlement.
    public HashSet<string> ConsumedRequestKeys { get; set; } = [];
}

public class FriendRequestReceipt
{
    public string Key { get; set; } = "";
    public byte[] Response { get; set; } = [];
}

public class FriendGiftHistory
{
    public int SentDay { get; set; }
    public int ClaimedDay { get; set; }
}
