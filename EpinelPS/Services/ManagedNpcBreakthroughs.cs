using System.Runtime.CompilerServices;
using EpinelPS.Data;

namespace EpinelPS.Services;

internal static class ManagedNpcBreakthroughs
{
    private sealed record Variants(int BaseGradeCoreId, Dictionary<int, CharacterRecord> ByGradeCoreId);
    private static readonly ConditionalWeakTable<GameData, Dictionary<int, Variants>> Cache = new();

    private static (int Tid, int Grade) Target(CharacterModel character)
    {
        var data = GameData.Instance;
        if (!data.CharacterTable.TryGetValue(character.Tid, out var current))
            throw new InvalidDataException($"Unknown NPC character template: {character.Tid}.");
        var variants = Cache.GetValue(data, d => d.CharacterTable.Values.GroupBy(c => c.NameCode)
            .ToDictionary(g => g.Key, g => new Variants(g.Min(c => c.GradeCoreId), g.GroupBy(c => c.GradeCoreId)
                .ToDictionary(v => v.Key, v => v.OrderBy(c => c.Id).First()))))[current.NameCode];
        int templateGrade = current.GradeCoreId - variants.BaseGradeCoreId;
        int grade = Math.Max(character.Grade, templateGrade);
        // Preserve the existing NPC level policy and never lower an earned core upgrade.
        if (current.OriginalRare == OriginalRareType.SSR && character.Level > 160) grade = Math.Max(3, grade);
        if (grade == templateGrade) return (current.Id, grade);
        if (!variants.ByGradeCoreId.TryGetValue(variants.BaseGradeCoreId + grade, out var target))
            throw new InvalidDataException($"NPC character {character.Csn} has no grade-{grade} variant for NameCode {current.NameCode}.");
        return (target.Id, grade);
    }

    internal static bool NeedsRepair(User user) => user.ManagedNpc != null && user.Characters.Any(c =>
    {
        var target = Target(c);
        return c.Tid != target.Tid || c.Grade != target.Grade;
    });

    internal static void Apply(CharacterModel character)
    {
        var target = Target(character);
        character.Tid = target.Tid;
        character.Grade = target.Grade;
    }

    internal static void Apply(User user)
    {
        if (user.ManagedNpc == null) return;
        foreach (var character in user.Characters) Apply(character);
    }

    internal static bool TryAdvance(CharacterModel character)
    {
        var current = Target(character);
        var table = GameData.Instance.CharacterTable;
        var row = table[current.Tid];
        if (!table.TryGetValue(row.GrowGrade, out var next) || next.NameCode != row.NameCode || next.GradeCoreId != row.GradeCoreId + 1) return false;
        character.Tid = next.Id;
        character.Grade = current.Grade + 1;
        return true;
    }
}
