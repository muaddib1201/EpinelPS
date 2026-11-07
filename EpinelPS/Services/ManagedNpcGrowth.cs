using EpinelPS.Data;
using EpinelPS.LobbyServer.Character;

namespace EpinelPS.Services;

public static class ManagedNpcGrowth
{
    public const int SchemaVersion = 1;
    public static int Level(User user, CharacterModel character) => user.GetCharacterLevel(character.Csn, character.Level);
    public static int SynchroLevel(User user) => user.GetSynchroLevel();
    public static int MaximumSynchroLevel(User user) => MaximumSynchroLevel(user.Characters);
    private static int MaximumSynchroLevel(IReadOnlyCollection<CharacterModel> characters) => Math.Min(GameData.Instance.LevelData.Keys.Max(),
        200 + characters.Count + (int)(characters.Sum(c => Math.Min(c.Grade, 3)) * 1.334));

    private static int BaseCap(CharacterModel c) => GameData.Instance.CharacterTable[c.Tid].OriginalRare switch
    {
        OriginalRareType.SSR => c.Grade >= 3 ? 200 : 160,
        OriginalRareType.SR => 160,
        _ => 80
    };

    public static void Apply(User user)
    {
        if (user.ManagedNpc == null) return;
        var previousLevels = user.Characters.ToDictionary(c => c.Csn, c => Level(user, c));
        var planned = user.Characters.Select(c => new CharacterModel { Csn = c.Csn, Tid = c.Tid, Grade = c.Grade, Level = c.Level }).ToArray();
        foreach (var c in planned)
        {
            ManagedNpcBreakthroughs.Apply(c);
            c.Level = Math.Min(c.Level, BaseCap(c));
        }
        bool eligible = planned.Where(c => c.Level == 200 && c.Grade >= 3 &&
            GameData.Instance.CharacterTable[c.Tid].OriginalRare == OriginalRareType.SSR)
            .Select(c => GameData.Instance.CharacterTable[c.Tid].NameCode).Distinct().Count() >= 5;
        if (user.SynchroDeviceUpgraded && !eligible)
            throw new InvalidDataException("NPC enhanced synchro requires five distinct three-star level-200 SSRs.");
        int target = eligible ? Math.Max(200, previousLevels.Values.DefaultIfEmpty(user.GetSynchroLevel()).Max())
            : planned.OrderByDescending(c => c.Level).Take(5).Select(c => c.Level).DefaultIfEmpty(1).Min();
        if (eligible && target > MaximumSynchroLevel(planned))
            throw new InvalidDataException("NPC legacy synchro level exceeds its collection limit.");
        var standards = eligible ? new HashSet<int>() : planned.OrderByDescending(c => c.Level).ThenBy(c => c.Csn).Take(5).Select(c => c.Csn).ToHashSet();
        if (planned.Any(c => (standards.Contains(c.Csn) ? c.Level : target) < previousLevels[c.Csn]))
            throw new InvalidDataException("NPC legacy levels cannot be migrated without losing effective levels; existing state retained.");
        var byCsn = planned.ToDictionary(c => c.Csn);
        foreach (var c in user.Characters)
        {
            var replacement = byCsn[c.Csn];
            c.Tid = replacement.Tid;
            c.Grade = replacement.Grade;
            c.Level = replacement.Level;
        }
        if (eligible)
        {
            user.SynchroDeviceUpgraded = true;
            user.SynchroDeviceLevel = target;
        }
        EnsureSlots(user);
        user.ManagedNpc.GrowthSchemaVersion = SchemaVersion;
    }

    private static void EnsureSlots(User user)
    {
        var standards = user.SynchroDeviceUpgraded ? new HashSet<int>() : user.Characters
            .OrderByDescending(c => c.Level).ThenBy(c => c.Csn).Take(5).Select(c => c.Csn).ToHashSet();
        var desired = user.Characters.Where(c => !standards.Contains(c.Csn)).Select(c => (long)c.Csn).ToHashSet();
        var assigned = new HashSet<long>();
        foreach (var slot in user.SynchroSlots)
            if (!desired.Contains(slot.CharacterSerialNumber) || !assigned.Add(slot.CharacterSerialNumber)) slot.CharacterSerialNumber = 0;
        foreach (long csn in desired.Order())
        {
            if (assigned.Contains(csn)) continue;
            var slot = user.SynchroSlots.FirstOrDefault(s => s.CharacterSerialNumber == 0) ?? SynchroAddSlot.AppendSlot(user);
            slot.CharacterSerialNumber = csn;
            slot.AvailableAt = 0;
            assigned.Add(csn);
        }
        int missingAllowance = user.SynchroSlots.Count - SynchroAddSlot.GetFreeSlotAllowance(user);
        if (missingAllowance > 0) user.SynchroSlotsFromItems += missingAllowance;
    }

    public static void Advance(User user)
    {
        if (user.ManagedNpc == null) return;
        Apply(user);
        var levels = GameData.Instance.LevelData;
        if (user.SynchroDeviceUpgraded)
        {
            if (user.SynchroDeviceLevel < MaximumSynchroLevel(user) && levels.ContainsKey(user.SynchroDeviceLevel + 1))
                user.SynchroDeviceLevel++;
        }
        else
        {
            foreach (var c in user.Characters)
                if (c.Level < BaseCap(c) && levels.ContainsKey(c.Level + 1)) c.Level++;
            Apply(user);
        }
    }

    public static string? Recruit(User user, Random rng)
    {
        if (user.ManagedNpc == null) return null;
        var table = GameData.Instance.CharacterTable;
        var owned = user.Characters.Select(c => table[c.Tid].NameCode).ToHashSet();
        var fresh = ManagedNpcFactory.Characters.Where(c => c.OriginalRare == OriginalRareType.SSR && !owned.Contains(c.NameCode)).ToArray();
        var duplicates = user.Characters.Where(c => table[c.Tid].OriginalRare == OriginalRareType.SSR && c.Grade < 10).ToArray();
        bool collect = user.ManagedNpc.RecruitSequence++ % 2 == 0;
        if ((collect && fresh.Length > 0) || duplicates.Length == 0 && fresh.Length > 0)
        {
            ManagedNpcFactory.AddCharacter(user, fresh[rng.Next(fresh.Length)], 1, 1);
            Apply(user);
            return "招募新妮姬并编入同步器";
        }
        if (duplicates.Length == 0) return null;
        var mains = duplicates.Where(c => user.RepresentationTeamDataNew.Contains(c.Csn)).ToArray();
        var candidates = mains.Length > 0 ? mains : duplicates;
        if (!user.SynchroDeviceUpgraded && candidates.Any(c => c.Grade < 3)) candidates = candidates.Where(c => c.Grade < 3).ToArray();
        int lowest = candidates.Min(c => c.Grade);
        candidates = candidates.Where(c => c.Grade == lowest).ToArray();
        var character = candidates[rng.Next(candidates.Length)];
        if (!ManagedNpcBreakthroughs.TryAdvance(character)) throw new InvalidDataException("NPC duplicate has no next breakthrough variant.");
        Apply(user);
        return character.Grade <= 3 ? $"重复招募：主力突破至 {character.Grade} 星" : $"重复招募：主力核心强化 +{character.Grade - 3}";
    }
}
