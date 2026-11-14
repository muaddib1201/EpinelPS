using EpinelPS.Data;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.LobbyServer.Soloraid;
using EpinelPS.Utils;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    private sealed record NpcRaidStage(SoloRaidPresetRecord Preset, long Hp, int Seconds);
    private sealed record NpcRaidCandidate(CharacterRecord Row, TeamCharacterData Snapshot);
    private sealed record NpcRaidSquad(TeamCharacterData[] Characters, decimal Efficiency);

    private static void RunSoloRaidDaily(User user, DateTime now)
    {
        if (user.ManagedNpc is not { } npc || user.IsBanned || !DailyUnlocked(user, ContentsOpen.SoloRaid)) return;
        var utc = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        var season = SoloRaidSeasonSource.Current(utc);
        if (season is null || SoloRaidHelper.GetPeriodResult(season.RaidId, utc) != SoloRaidPeriodResult.Success) return;
        int day = GuildActivity.Day(now);
        if (npc.SoloRaidRoutine is { } completed && (now < completed.CompletedUtc ||
            completed.RaidId == season.RaidId && completed.Day >= day)) return;
        if (!TryGetNpcRaidStages(season.RaidId, out var stages)) return;

        // SoloRaidInfo uses User.GetDateDay's UTC+4 date label, while the daily routine
        // uses GuildActivity's reset-day label. Keep both existing formats intact.
        int raidDay = int.Parse(now.AddHours(4).ToString("yyyyMMdd"));
        user.SoloRaidData.TryGetValue(season.RaidId, out var saved);
        if (saved is not null && (saved.LastDateDay > raidDay || saved.SoloRaidLevels.Any(l => l.IsOpen))) return;
        int maxTeams = NpcRaidSetting("MaxSoloRaidTeamCount", 5, 5);
        var normalSquads = NpcRaidSquads(user, null, maxTeams);
        var challengeSquads = NpcRaidSquads(user, stages[7].Preset.CharacterLv > 0 ? stages[7].Preset.CharacterLv : null, maxTeams);
        if (normalSquads.Count == 0 || challengeSquads.Count == 0) return;

        // Own the save once, together with rewards and the replay checkpoint. The client
        // open/close helpers save independently, so they cannot be used inside this boundary.
        SettleDailyPhase(user, now, false, () =>
        {
            var raid = saved ?? new SoloRaidInfo { RaidId = season.RaidId, LastDateDay = raidDay };
            user.SoloRaidData[season.RaidId] = raid;
            if (raid.LastDateDay < raidDay)
            {
                raid.RaidOpenCount = raid.TrialCount = 0;
                raid.LastDateDay = raidDay;
            }
            int normalBefore = raid.RaidOpenCount, trialBefore = raid.TrialCount, clears = 0;
            int normalLimit = NpcRaidSetting("SoloRaidEnterCount", 3, 20);
            while (raid.RaidOpenCount < normalLimit)
            {
                var stage = stages.Take(7).FirstOrDefault(s => !NpcRaidCleared(raid, s.Preset.WaveOrder));
                if (stage is null || !NpcRaidPrerequisite(raid, stage.Preset, stages)) break;
                var run = SimulateNpcRaid(user, stage, normalSquads, day, raid.RaidOpenCount, false);
                raid.RaidOpenCount++;
                raid.SoloRaidLevels.RemoveAll(l => l.Type == SoloRaidType.Normal && l.RaidLevel == run.RaidLevel && !l.IsOpen);
                raid.SoloRaidLevels.Add(run);
                if (!run.IsClear) break; // A stronger roster, rather than repeated free attempts, is needed.
                clears++;
                if (stage.Preset.RewardId > 0) RewardUtils.RegisterRewardsForUser(user, stage.Preset.RewardId);
                if (stage.Preset.FirstClearRewardId > 0) RewardUtils.RegisterRewardsForUser(user, stage.Preset.FirstClearRewardId);
            }
            if (stages.Take(7).All(s => NpcRaidCleared(raid, s.Preset.WaveOrder)) && NpcRaidPrerequisite(raid, stages[7].Preset, stages))
            {
                int trialLimit = NpcRaidSetting("SoloRaidTrialEnterCount", 3, 20);
                while (raid.TrialCount < trialLimit)
                {
                    var run = SimulateNpcRaid(user, stages[7], challengeSquads, day, raid.TrialCount, true);
                    raid.TrialCount++;
                    var best = raid.SoloRaidLevels.Where(l => l.Type == SoloRaidType.Trial && l.RaidLevel == 8 && l.IsClear && !l.IsOpen)
                        .MaxBy(l => l.TotalDamage);
                    if (best is null || run.TotalDamage > best.TotalDamage)
                    {
                        raid.SoloRaidLevels.RemoveAll(l => l.Type == SoloRaidType.Trial && l.RaidLevel == 8 && !l.IsOpen);
                        raid.SoloRaidLevels.Add(run);
                    }
                }
            }
            npc.SoloRaidRoutine = new NpcSoloRaidRoutineState { RaidId = season.RaidId, Day = day, CompletedUtc = now };
            npc.LastActionUtc = user.LastLogin = now;
            npc.TotalActions++; npc.Sequence++;
            long damage = raid.SoloRaidLevels.Where(l => l.Type == SoloRaidType.Trial && l.IsClear && !l.IsOpen)
                .Select(l => l.TotalDamage).DefaultIfEmpty(0).Max();
            DailyLog(user, now, $"单人突袭 {season.RaidId}：普通参战 {raid.RaidOpenCount - normalBefore} 次、通关 {clears} 层；挑战 {raid.TrialCount - trialBefore} 次，赛季最高伤害 {damage:N0}");
        });
    }

    private static bool NpcRaidCleared(SoloRaidInfo raid, int level) =>
        raid.SoloRaidLevels.Any(l => l.Type == SoloRaidType.Normal && l.RaidLevel == level && l.IsClear && !l.IsOpen);

    private static bool NpcRaidPrerequisite(SoloRaidInfo raid, SoloRaidPresetRecord preset, NpcRaidStage[] stages) =>
        preset.WaveOpenCondition == 0 || stages.Any(s => s.Preset.Id == preset.WaveOpenCondition && NpcRaidCleared(raid, s.Preset.WaveOrder));

    private static int NpcRaidSetting(string key, int fallback, int maximum) =>
        int.TryParse(GameData.Instance.GameSettings.GetValueOrDefault(key)?.Value, out int value) && value >= 0
            ? Math.Min(value, maximum) : fallback;

    private static bool TryGetNpcRaidStages(int raidId, out NpcRaidStage[] stages)
    {
        stages = [];
        var data = GameData.Instance;
        if (!data.SoloRaidManagerTable.TryGetValue(raidId, out var manager)) return false;
        var presets = data.SoloRaidPresetTable.Values.Where(p => p.PresetGroupId == manager.MonsterPreset).ToArray();
        var result = new List<NpcRaidStage>();
        for (int level = 1; level <= 8; level++)
        {
            var matches = presets.Where(p => p.WaveOrder == level).ToArray();
            if (matches.Length != 1) return false;
            var preset = matches[0];
            if (preset.WaveOpenCondition != 0 && !presets.Any(p => p.Id == preset.WaveOpenCondition && p.WaveOrder < level)) return false;
            if (!data.WaveIntercept001Table.TryGetValue(preset.Wave, out var wave) || wave.TargetList is not { Count: > 0 } ||
                wave.TargetList.Any(id => !data.MonsterTable.ContainsKey(id))) return false;
            var monster = data.MonsterTable[wave.TargetList[0]];
            decimal hp = data.MonsterStatEnhanceTable.Values.Where(s => s.GroupId == monster.StatenhanceId && s.Lv == preset.MonsterStageLv)
                .Sum(s => (decimal)s.LevelHp);
            if (hp <= 0 || hp > long.MaxValue / 20) return false;
            if (new[] { preset.RewardId, preset.FirstClearRewardId }.Any(id => id > 0 && data.GetRewardTableEntry(id) == null)) return false;
            result.Add(new(preset, (long)hp, wave.BattleTime > 0 ? Math.Min(3600, wave.BattleTime) : 180));
        }
        stages = result.ToArray();
        return true;
    }

    private static List<NpcRaidSquad> NpcRaidSquads(User user, int? fixedLevel, int maxTeams)
    {
        var data = GameData.Instance;
        var primary = user.RepresentationTeamDataNew.ToHashSet();
        var pool = user.Characters.Where(c => data.CharacterTable.TryGetValue(c.Tid, out var row) && row.IsVisible)
            .Select(c => new NpcRaidCandidate(data.CharacterTable[c.Tid], new TeamCharacterData
            {
                Csn = c.Csn, Tid = c.Tid, CostumeId = c.CostumeId,
                Lv = fixedLevel ?? user.GetCharacterLevel(c.Csn, c.Level), Combat = FormulaUtils.CalculateCP(user, c.Csn, fixedLevel)
            })).Where(c => c.Snapshot.Combat > 0)
            .GroupBy(c => c.Row.NameCode).Select(g => g.OrderByDescending(c => primary.Contains(c.Snapshot.Csn))
                .ThenByDescending(c => c.Snapshot.Combat).ThenBy(c => c.Snapshot.Csn).First())
            .OrderByDescending(c => c.Snapshot.Combat).ThenBy(c => c.Snapshot.Csn).ToList();
        var result = new List<NpcRaidSquad>();
        while (pool.Count > 0 && result.Count < maxTeams)
        {
            var members = new List<NpcRaidCandidate>();
            void Add(NpcRaidCandidate? candidate)
            {
                if (candidate is null || members.Count == 5 || !pool.Remove(candidate)) return;
                members.Add(candidate);
            }
            if (result.Count == 0)
                foreach (long csn in user.RepresentationTeamDataNew.Distinct()) Add(pool.FirstOrDefault(c => c.Snapshot.Csn == csn));
            else
                foreach (var step in new[] { BurstStep.Step1, BurstStep.Step2, BurstStep.Step3, BurstStep.Step3 })
                    Add(pool.FirstOrDefault(c => c.Row.UseBurstSkill == step));
            foreach (var candidate in pool.ToArray()) Add(candidate);
            long[] serials = members.Select(c => c.Snapshot.Csn).ToArray();
            int cycle = new[] { 20, 40, 60 }.FirstOrDefault(seconds => ManagedNpcTeams.CanCycle(user, serials, seconds));
            decimal efficiency = cycle > 0 ? 20m / cycle : .35m;
            var characters = members.Select((c, i) => { c.Snapshot.Slot = i + 1; return c.Snapshot; }).ToArray();
            result.Add(new(characters, efficiency));
        }
        return result;
    }

    private static SoloRaidLevelData SimulateNpcRaid(User user, NpcRaidStage stage, List<NpcRaidSquad> squads, int day, int attempt, bool trial)
    {
        var result = new SoloRaidLevelData { RaidLevel = stage.Preset.WaveOrder, Type = trial ? SoloRaidType.Trial : SoloRaidType.Normal, Hp = stage.Hp };
        uint variation = unchecked((uint)(user.ManagedNpc!.Seed ^ day * 397 ^ stage.Preset.Id * 7919 ^ attempt * 104729));
        decimal execution = .95m + variation % 101 / 1000m;
        foreach (var squad in squads)
        {
            // Deliberately an NPC power estimate, not a client battle-engine result.
            // CP -> estimated DPS (12x), then the actual wave time, burst cadence and
            // a seeded 95%-105% execution factor. Skills/cores enter through the CP formula.
            decimal estimate = squad.Characters.Sum(c => (long)c.Combat) * 12m * stage.Seconds * squad.Efficiency * execution;
            long damage = (long)Math.Clamp(estimate, 1, long.MaxValue / 20);
            if (!trial) damage = Math.Min(damage, stage.Hp - result.TotalDamage);
            result.TotalDamage += damage;
            bool kill = !trial && result.TotalDamage >= stage.Hp;
            result.Logs.Add(new SoloRaidLogData { Damage = damage, Kill = kill,
                Team = squad.Characters.Select(c => new TeamCharacterData { Slot = c.Slot, Csn = c.Csn, Tid = c.Tid, Lv = c.Lv, Combat = c.Combat, CostumeId = c.CostumeId }).ToList() });
            if (kill) break;
        }
        result.RaidJoinCount = result.Logs.Count;
        // Ending with the available squads is valid, just as an early client trial close is.
        result.IsClear = trial || result.TotalDamage >= result.Hp;
        result.IsOpen = false;
        result.Status = result.IsClear ? SoloRaidStatus.Kill : SoloRaidStatus.Alive;
        return result;
    }
}
