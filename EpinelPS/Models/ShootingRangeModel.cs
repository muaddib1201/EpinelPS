namespace EpinelPS.Models;

public class SavedShootingRangeBattle
{
    public long GuildId { get; set; }
    // Store the protobuf snapshot rather than references to mutable team/character objects.
    public byte[] BattleLog { get; set; } = [];
    public string? RequestKey { get; set; }
    public byte[] Receipt { get; set; } = [];
}
