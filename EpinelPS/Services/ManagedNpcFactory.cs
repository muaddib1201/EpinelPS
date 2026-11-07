using EpinelPS.Data;

namespace EpinelPS.Services;

internal static class ManagedNpcFactory
{
    internal static CampaignStageRecord[] Stages => GameData.Instance.StageDataRecords.Values
        .Where(s => s.ChapterMod == ChapterMod.Normal && s.StageType == StageType.Main &&
            s.StageCategory is StageCategory.Normal or StageCategory.Boss).OrderBy(s => s.ChapterId).ThenBy(s => s.Id).ToArray();
    internal static CharacterRecord[] Characters => GameData.Instance.CharacterTable.Values
        .Where(c => c.IsVisible && c.OriginalRare is OriginalRareType.SSR or OriginalRareType.SR or OriginalRareType.R)
        .GroupBy(c => c.NameCode).Select(g => g.OrderBy(c => c.GradeCoreId).ThenBy(c => c.Id).First()).OrderBy(c => c.Id).ToArray();

    internal static User Create(ulong id, int ordinal, DateTime now)
    {
        var profile = ManagedNpcProfiles.Get(ordinal);
        int seed = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var rng = new Random(seed);
        int rosterTier = ordinal % 5;
        int tier = ManagedNpcService.ActiveTier;
        int chapter = Math.Min(24, 3 + rosterTier * 4 + rng.Next(6));
        var stages = Stages;
        var candidates = stages.Where(s => s.ChapterId <= chapter).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("No valid NPC campaign stages.");
        var target = candidates[Math.Max(0, candidates.Length - 1 - rng.Next(Math.Min(12, candidates.Length)))];
        var user = new User
        {
            ID = id, Description = profile.Description, LastLogin = now.AddMinutes(-rng.Next(30, 1440)),
            ProfileIconId = GameData.Instance.UserIconTable.Keys.Order().ElementAt(rng.Next(GameData.Instance.UserIconTable.Count)),
            ManagedNpc = new ManagedNpcState
            {
                Name = profile.Name,
                Seed = seed, Tier = tier, CreatedUtc = now, ActiveStartHourUtc = rng.Next(0, 9),
                NextActionUtc = now.AddMinutes(rng.Next(1, 61)), LastActionUtc = now.AddMinutes(-rng.Next(30, 1440)),
                NextRecruitUtc = now.AddDays(rng.Next(3, 8)), NextSkillUtc = now.AddDays(rng.Next(1, 8))
            }
        };
        user.userPointData.UserLevel = Math.Min(GameData.Instance.UserExpDataRecords.Keys.Max(), 15 + chapter * 5 + rng.Next(15));
        int level = Math.Min(GameData.Instance.LevelData.Keys.Max(), 20 + chapter * 8 + rng.Next(12));
        var pool = Characters.Where(c => c.OriginalRare == OriginalRareType.SSR &&
            GameData.Instance.CharacterSkills.TryGetValue(c.UltiSkillId, out var skill) && skill.SkillCooltime > 0)
            .OrderBy(_ => rng.Next()).ToList();
        var selected = new List<CharacterRecord>();
        // Give new inventories a genuine interacting core; the selector may
        // improve it using their other owned cards. Existing NPCs never receive
        // these cards during migration.
        int[][] cores =
        [
            [5011, 5065, 5099, 5105, 5103], // Liter, Crown/Naga, two damage dealers.
            [5049, 5008, 5036, 5009, 5041], // 777 companions with an unconditional II backup.
            [5100, 5075, 5099, 5004, 5105], // Tia/Naga and charge synergy.
            [5011, 5116, 5117, 5066, 5024], // Summer sustained damage and healing.
            [5081, 5092, 5106, 5113, 5048]  // Shotgun support, damage and healing.
        ];
        var core = cores[rng.Next(cores.Length)].Select(name => pool.FirstOrDefault(c => c.NameCode == name)).ToArray();
        if (core.All(c => c != null))
        {
            selected.AddRange(core.OfType<CharacterRecord>());
            pool.RemoveAll(c => selected.Contains(c));
        }
        // Seed a conservative 20-second advancing I/II and two alternating III.
        // No conditional cooldown reduction or re-entry assumptions are needed.
        if (selected.Count == 0)
        {
            foreach (var burst in new[] { BurstStep.Step1, BurstStep.Step2, BurstStep.Step3 })
            {
                var row = pool.First(c => c.UseBurstSkill == burst && (int)c.ChangeBurstStep == (int)burst + 1 &&
                    GameData.Instance.CharacterSkills[c.UltiSkillId].SkillCooltime <= (burst == BurstStep.Step3 ? 4000 : 2000));
                selected.Add(row); pool.Remove(row);
            }
            var second = pool.First(c => c.UseBurstSkill == BurstStep.Step3 && c.ChangeBurstStep == BurstStep.StepFull &&
                GameData.Instance.CharacterSkills[c.UltiSkillId].SkillCooltime <= 4000);
            selected.Add(second); pool.Remove(second);
        }
        int[] minimum = [8, 13, 19, 25, 31], maximum = [12, 18, 24, 30, 40];
        int count = rng.Next(minimum[rosterTier], maximum[rosterTier] + 1);
        selected.AddRange(pool.Take(count - selected.Count));
        foreach (var row in selected) AddCharacter(user, row, level, 1);
        ManagedNpcRoster.Apply(user);
        ManagedNpcGrowth.Apply(user);
        foreach (var stage in stages.TakeWhile(s => s.Id != target.Id).Append(target)) CompleteStage(user, stage);
        user.MainQuestData[21] = true; user.MainQuestData[25] = true;
        user.OutpostBattleLevel.Level = Math.Min(GameData.Instance.OutpostBattle.Count, 1 + candidates.Length / 5);
        user.InfraCoreLvl = 1 + chapter / 4;
        return user;
    }

    internal static void AddCharacter(User user, CharacterRecord row, int level, int skill)
    {
        var character = new CharacterModel
        {
            Csn = user.Characters.Select(c => c.Csn).DefaultIfEmpty(10000).Max() + 1,
            Tid = row.Id, Level = level, Skill1Lvl = skill, Skill2Lvl = skill, UltimateLevel = skill
        };
        ManagedNpcBreakthroughs.Apply(character);
        user.Characters.Add(character);
    }

    internal static void CompleteStage(User user, CampaignStageRecord stage)
    {
        string map = GameData.Instance.GetMapIdFromChapter(stage.ChapterId, stage.ChapterMod);
        if (!user.FieldInfoNew.TryGetValue(map, out var field)) user.FieldInfoNew[map] = field = new();
        if (!field.CompletedStages.Contains(stage.Id)) field.CompletedStages.Add(stage.Id);
        user.LastNormalStageCleared = stage.Id;
        user.LastClearedDifficulty = (int)ChapterMod.Normal;
        foreach (var scene in new[] { stage.EnterScenario, stage.ExitScenario })
            if (!string.IsNullOrWhiteSpace(scene) && !user.CompletedScenarios.Contains(scene)) user.CompletedScenarios.Add(scene);
    }
}
