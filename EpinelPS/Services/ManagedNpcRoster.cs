using EpinelPS.Data;
using EpinelPS.LobbyServer.Arena;

namespace EpinelPS.Services;

public static class ManagedNpcRoster
{
    public const int SchemaVersion = 1;
    public static readonly string[] TierNames = ["起步", "初成", "进阶", "成熟", "资深"];
    private static readonly int[] MainMinimum = [2, 4, 6, 8, 9];
    private static readonly int[] MainMaximum = [3, 5, 7, 9, 10];
    public static int TierForCount(int count) => count <= 12 ? 0 : count <= 18 ? 1 : count <= 24 ? 2 : count <= 30 ? 3 : 4;
    public static int SsrCount(User user) => user.Characters.Where(c => GameData.Instance.CharacterTable.TryGetValue(c.Tid, out var row)
        && row.OriginalRare == OriginalRareType.SSR).Select(c => GameData.Instance.CharacterTable[c.Tid].NameCode).Distinct().Count();
    public static string SkillRange(int tier) => $"{MainMinimum[Math.Clamp(tier, 0, 4)]}～{MainMaximum[Math.Clamp(tier, 0, 4)]}";
    public static int SkillCap(User user) => MainMaximum[TierForCount(SsrCount(user))];

    public static void Apply(User user) => ApplyCore(user, true);
    public static void Refresh(User user) => ApplyCore(user, false);

    private static void ApplyCore(User user, bool initializeSkills)
    {
        if (user.ManagedNpc == null) return;
        var plan = ManagedNpcTeams.Select(user);
        var state = user.ManagedNpc;
        state.RosterTier = TierForCount(SsrCount(user));
        state.BurstCycleSeconds = plan.CycleSeconds;
        state.TeamSynergies = plan.Synergies.ToList();
        user.RepresentationTeamDataNew = plan.SerialNumbers;
        var main = plan.SerialNumbers.ToHashSet();
        foreach (var c in user.Characters.Where(_ => initializeSkills))
        {
            if (!GameData.Instance.CharacterTable.TryGetValue(c.Tid, out var row) || row.OriginalRare != OriginalRareType.SSR) continue;
            bool active = main.Contains(c.Csn);
            int low = active ? MainMinimum[state.RosterTier] : 1 + state.RosterTier;
            int high = active ? MainMaximum[state.RosterTier] : 2 + state.RosterTier;
            int Target(int slot) => low + (int)(unchecked((uint)(state.Seed ^ row.NameCode * 397 ^ slot * 7919)) % (uint)(high - low + 1));
            c.Skill1Lvl = Math.Max(c.Skill1Lvl, Target(1));
            c.Skill2Lvl = Math.Max(c.Skill2Lvl, Target(2));
            c.UltimateLevel = Math.Max(c.UltimateLevel, Target(3));
        }
        var campaign = user.UserTeams.GetValueOrDefault(1)?.Clone() ?? new NetUserTeamData { Type = 1 };
        var old = campaign.Teams.FirstOrDefault(t => t.TeamNumber == 1);
        if (old != null) campaign.Teams.Remove(old);
        campaign.Teams.Add(Team(plan.SerialNumbers)); campaign.LastContentsTeamNumber = 1;
        user.UserTeams[1] = campaign;
        foreach (int type in new[] { 3, 4 })
            user.UserTeams[type] = new NetUserTeamData { Type = type, LastContentsTeamNumber = 1, Teams = { Team(plan.SerialNumbers) } };
        // Existing special squads are otherwise accepted unchanged by ArenaTeams.Ensure.
        foreach (int type in new[] { 12, 13 })
            if (user.UserTeams.Remove(type)) ArenaTeams.Ensure(user, true, type == 13);
        state.TeamSchemaVersion = SchemaVersion;
    }

    private static NetTeamData Team(long[] serialNumbers)
    {
        var team = new NetTeamData { TeamNumber = 1 };
        for (int i = 0; i < serialNumbers.Length; i++) team.Slots.Add(new NetTeamSlot { Slot = i + 1, Value = serialNumbers[i] });
        return team;
    }
}
