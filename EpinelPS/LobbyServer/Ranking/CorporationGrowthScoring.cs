using EpinelPS.Data;
using EpinelPS.Models;

namespace EpinelPS.LobbyServer.Ranking;

internal static class CorporationGrowthScoring
{
    // Client HELP_POINT_RANKING_03_02..05: rarity, manufacturer/class
    // research, character levels, and three skill levels. This is not CP.
    internal static int[] Scores(User user)
    {
        var data = GameData.Instance;
        var knownCharacters = user.Characters.Where(c => data.CharacterTable.ContainsKey(c.Tid)).ToArray();
        var research = user.ResearchProgress
            .Where(p => data.RecycleResearchStats.ContainsKey(p.Key))
            .Select(p => (Definition: data.RecycleResearchStats[p.Key], Level: Math.Max(0L, p.Value.Level)))
            .Where(p => p.Definition.RecycleType is RecycleType.Class or RecycleType.Corporation)
            .GroupBy(p => (p.Definition.RecycleType, p.Definition.RecycleSubType))
            .ToDictionary(g => g.Key, g => g.Max(p => p.Level));
        var registered = user.SynchroSlots.Where(s => s.Slot > 0 && s.CharacterSerialNumber > 0)
            .Select(s => s.CharacterSerialNumber).ToHashSet();
        // Unknown saved definitions must not influence the five native levels,
        // even indirectly through an otherwise valid registered character.
        long synchro = registered.Count == 0 ? 0 : Math.Max(0L, user.SynchroDeviceUpgraded
            ? user.SynchroDeviceLevel : knownCharacters.OrderByDescending(c => c.Level).Take(5).LastOrDefault()?.Level ?? 1);
        var characters = knownCharacters
            .Select(c =>
            {
                var definition = data.CharacterTable[c.Tid];
                // Native and registered Synchro are separate level contributions:
                // a native-level-1 character entering Synchro 350 adds 350 points.
                long levels = Math.Max(0L, c.Level) + (registered.Contains(c.Csn) ? synchro : 0);
                long skills = Skill(c.Skill1Lvl) + Skill(c.Skill2Lvl) + Skill(c.UltimateLevel);
                long room = research.GetValueOrDefault((RecycleType.Corporation, Manufacturer(definition.Corporation))) +
                    research.GetValueOrDefault((RecycleType.Class, Class(definition.Class)));
                return (Definition: definition, Points: Rarity(definition) + levels + skills + room);
            })
            .GroupBy(c => c.Definition.NameCode)
            .Select(g => g.MaxBy(c => c.Points));
        var totals = new long[5];
        foreach (var character in characters)
        {
            int type = character.Definition.Corporation switch
            {
                CorporationType.MISSILIS => 1, CorporationType.ELYSION => 2,
                CorporationType.TETRA => 3, CorporationType.PILGRIM => 4, _ => 0
            };
            if (type > 0) totals[type] = Math.Min(int.MaxValue, totals[type] + character.Points);
        }
        return totals.Select(value => (int)value).ToArray();

        long Skill(int level) => data.SkillLevelPoints.TryGetValue(level, out var row) ? Math.Max(0, row.Point) : 0;
        long Rarity(CharacterRecord character)
        {
            int baseGrade = character.OriginalRare switch { OriginalRareType.SSR => 1, OriginalRareType.SR => 101, _ => 201 };
            int value = character.GradeCoreId - baseGrade;
            if (value < 0) return 0;
            var row = data.RankingPoints.Values.FirstOrDefault(p => p.OriginalRare == character.OriginalRare &&
                p.Grade == Math.Min(value, 3) && p.Core == Math.Max(value - 3, 0));
            return Math.Max(0, row?.Point ?? 0);
        }
    }

    private static RecycleSubType Manufacturer(CorporationType type) => type switch
    {
        CorporationType.ELYSION => RecycleSubType.ELYSION, CorporationType.MISSILIS => RecycleSubType.MISSILIS,
        CorporationType.TETRA => RecycleSubType.TETRA, CorporationType.PILGRIM => RecycleSubType.PILGRIM,
        CorporationType.ABNORMAL => RecycleSubType.ABNORMAL, _ => RecycleSubType.None
    };

    private static RecycleSubType Class(CharacterClassType type) => type switch
    {
        CharacterClassType.Attacker => RecycleSubType.Attacker, CharacterClassType.Defender => RecycleSubType.Defender,
        CharacterClassType.Supporter => RecycleSubType.Supporter, _ => RecycleSubType.None
    };
}
