namespace EpinelPS.Models;

/// <summary>The support characters selected at entry, retained until settlement.</summary>
public class SupportCharacterBattle
{
    public int BattleId { get; set; }
    public int TeamNumber { get; set; }
    public List<int> CharacterIds { get; set; } = [];
}
