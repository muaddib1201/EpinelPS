using EpinelPS.Data;
using System.Globalization;

namespace EpinelPS.Services;

public sealed record NpcTeamPlan(long[] SerialNumbers, int CycleSeconds, string[] Synergies);

public static class ManagedNpcTeams
{
    private sealed record Candidate(CharacterModel Character, CharacterRecord Row, int Cooldown, WeaponType Weapon, double Score);

    private static Candidate? Describe(User user, CharacterModel character, int seed)
    {
        var data = GameData.Instance;
        if (!data.CharacterTable.TryGetValue(character.Tid, out var row) || !row.IsVisible || row.OriginalRare != OriginalRareType.SSR ||
            !data.CharacterSkills.TryGetValue(row.UltiSkillId, out var skill) || skill.SkillCooltime <= 0) return null;
        var weapon = data.CharacterShots.GetValueOrDefault(row.ShotId)?.WeaponType ?? WeaponType.None;
        uint variation = unchecked((uint)(seed ^ row.NameCode * 7919));
        double score = NpcSynergyRules.Quality(row.NameCode) + Math.Min(user.GetCharacterLevel(character.Csn, character.Level), 400) * .02
            + (character.Skill1Lvl + character.Skill2Lvl + character.UltimateLevel) * .15 + variation % 31 * .1;
        if (row.Class == CharacterClassType.Attacker) score += 8;
        return new(character, row, skill.SkillCooltime, weapon, score);
    }

    public static NpcTeamPlan Select(User user)
    {
        int seed = user.ManagedNpc?.Seed ?? 0;
        var pool = user.Characters.Select(c => Describe(user, c, seed)).OfType<Candidate>()
            .GroupBy(c => c.Row.NameCode).Select(g => g.OrderByDescending(c => c.Score).ThenBy(c => c.Character.Csn).First())
            .OrderByDescending(c => c.Score).ThenBy(c => c.Row.NameCode).ToArray();
        if (pool.Length < 5) throw new InvalidDataException("NPC 的有效 SSR 不足 5 位，无法组成主力队。");
        var pairs = new double[pool.Length, pool.Length];
        for (int a = 0; a < pool.Length; a++)
        for (int b = a + 1; b < pool.Length; b++)
            pairs[a, b] = NpcSynergyRules.Pair(pool[a].Row, pool[a].Weapon, pool[b].Row, pool[b].Weapon).Score;

        // Cadence is the primary constraint. A strong-looking slow team never
        // displaces an owned roster that can sustain the shorter interval.
        foreach (int seconds in new[] { 20, 40, 60 })
        {
            Candidate[]? best = null;
            double bestScore = double.NegativeInfinity;
            for (int a = 0; a < pool.Length - 4; a++)
            for (int b = a + 1; b < pool.Length - 3; b++)
            for (int c = b + 1; c < pool.Length - 2; c++)
            for (int d = c + 1; d < pool.Length - 1; d++)
            for (int e = d + 1; e < pool.Length; e++)
            {
                double score = pool[a].Score + pool[b].Score + pool[c].Score + pool[d].Score + pool[e].Score
                    + pairs[a, b] + pairs[a, c] + pairs[a, d] + pairs[a, e] + pairs[b, c]
                    + pairs[b, d] + pairs[b, e] + pairs[c, d] + pairs[c, e] + pairs[d, e];
                bool heals = NpcSynergyRules.Heals(pool[a].Row.NameCode) || NpcSynergyRules.Heals(pool[b].Row.NameCode)
                    || NpcSynergyRules.Heals(pool[c].Row.NameCode) || NpcSynergyRules.Heals(pool[d].Row.NameCode) || NpcSynergyRules.Heals(pool[e].Row.NameCode);
                if (heals) score += 60;
                if (score <= bestScore) continue;
                Candidate[] team = [pool[a], pool[b], pool[c], pool[d], pool[e]];
                if (!Enumerable.Range(1, 3).All(phase => team.Any(p => Matches(p.Row, phase) && Next(p.Row, phase) > phase))) continue;
                team = Order(team);
                if (!CanCycle(team, seconds, out int mask)) continue;
                if (!CanCycle(team, seconds, out mask, knownBuffCooldowns: true)) mask = 0;
                // A burst-only buff does not help if this slot never casts in
                // the recurring rotation. Potential pair scores above are only
                // an upper bound used to prune combinations.
                for (int first = 0; first < team.Length; first++)
                for (int second = first + 1; second < team.Length; second++)
                {
                    var pair = NpcSynergyRules.Pair(team[first].Row, team[first].Weapon, team[second].Row, team[second].Weapon);
                    if (pair.Score > 0 && !ActivePair(team, first, second, mask)) score -= pair.Score;
                }
                if (heals && !team.Select((p, i) => NpcSynergyRules.Heals(p.Row.NameCode) &&
                    (!NpcSynergyRules.BurstHealing(p.Row.NameCode) || (mask & (1 << i)) != 0)).Any(active => active)) score -= 60;
                if (score <= bestScore) continue;
                best = team; bestScore = score;
            }
            if (best != null)
            {
                var labels = new List<string>();
                CanCycle(best, seconds, out int mask, knownBuffCooldowns: true);
                for (int a = 0; a < best.Length; a++)
                for (int b = a + 1; b < best.Length; b++)
                {
                    var pair = NpcSynergyRules.Pair(best[a].Row, best[a].Weapon, best[b].Row, best[b].Weapon);
                    if (pair.Score > 0 && pair.Label != null && ActivePair(best, a, b, mask)) labels.Add(pair.Label);
                }
                return new(best.Select(c => (long)c.Character.Csn).ToArray(), seconds, labels.Distinct().ToArray());
            }
        }
        throw new InvalidDataException("NPC 的现有 SSR 无法组成可持续的爆裂接续，请检查角色库。");
    }

    private static Candidate[] Order(Candidate[] team) => team.OrderBy(c => c.Row.ChangeBurstStep == c.Row.UseBurstSkill ? 0
            : c.Row.NameCode == 5065 && team.Any(p => p.Row.NameCode == 5099) ? 1 : c.Row.UseBurstSkill == BurstStep.Step1 ? 2
            : c.Row.UseBurstSkill == BurstStep.Step2 ? 3 : c.Row.UseBurstSkill == BurstStep.Step3 ? 4 : 5)
        .ThenByDescending(c => c.Cooldown)
        .ThenByDescending(c => NpcSynergyRules.BurstHealing(c.Row.NameCode))
        .ThenByDescending(c => team.Where(other => other != c).Sum(other => Math.Max(0,
            NpcSynergyRules.Pair(c.Row, c.Weapon, other.Row, other.Weapon).Score)))
        .ThenByDescending(c => c.Score).ThenBy(c => c.Row.NameCode).ToArray();

    private static bool ActivePair(Candidate[] team, int a, int b, int mask)
    {
        int caster = NpcSynergyRules.Pair(team[a].Row, team[a].Weapon, team[b].Row, team[b].Weapon).BurstCaster;
        return caster == 0 || team.Select((c, i) => c.Row.NameCode == caster && (mask & (1 << i)) != 0).Any(active => active);
    }

    public static bool CanCycle(User user, long[] serialNumbers, int seconds)
    {
        if (serialNumbers.Length != 5 || serialNumbers.Distinct().Count() != 5 || seconds <= 0) return false;
        var team = new List<Candidate>();
        foreach (long csn in serialNumbers)
        {
            var character = user.Characters.FirstOrDefault(c => c.Csn == csn);
            var candidate = character == null ? null : Describe(user, character, user.ManagedNpc?.Seed ?? 0);
            if (candidate == null) return false;
            team.Add(candidate);
        }
        return team.Select(c => c.Row.NameCode).Distinct().Count() == 5 && CanCycle(team.ToArray(), seconds, out _);
    }

    private static bool Matches(CharacterRecord row, int phase) => (int)row.UseBurstSkill == phase || row.UseBurstSkill == BurstStep.AllStep;
    private static int Next(CharacterRecord row, int phase) => row.ChangeBurstStep switch
    {
        BurstStep.NextStep => phase + 1,
        BurstStep.KeepStep => phase,
        _ => (int)row.ChangeBurstStep
    };

    private static bool CanCycle(Candidate[] team, int seconds, out int burstMask, bool knownBuffCooldowns = false)
    {
        burstMask = 0;
        var ready = new long[5];
        var states = new Dictionary<string, int>();
        var masks = new List<int>();
        var cooldowns = team.Select(c => c.Cooldown).ToArray();
        if (knownBuffCooldowns && team.Any(c => c.Row.NameCode is 5009 or 5049))
            for (int i = 0; i < team.Length; i++)
                if (team[i].Row.NameCode == 5008)
                {
                    // This second simulation only determines who really gets to
                    // cast buffs. The feasibility proof above still uses raw
                    // cooldowns. Blanc's guaranteed 777 reduction can shadow a
                    // backup II that appeared active in that slower proof.
                    int skillId = team[i].Row.Skill2Id + Math.Clamp(team[i].Character.Skill2Lvl, 1, 10) - 1;
                    var info = GameData.Instance.skillInfoTable.GetValueOrDefault(skillId);
                    if (info?.DescriptionValueList is { Length: > 2 } values && decimal.TryParse(values[2].DescriptionValue,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out decimal reduction))
                        cooldowns[i] = Math.Max(1, cooldowns[i] - (int)Math.Round(reduction * 100));
                    else cooldowns[i] = Math.Min(cooldowns[i], seconds * 100); // Unknown reduction: never credit a shadowed backup.
                }
        for (int cycle = 0; cycle < 64; cycle++)
        {
            long at = (long)cycle * seconds * 100; // Table cooldowns are centiseconds.
            string state = string.Join(',', ready.Select(t => Math.Max(0, t - at)));
            if (states.TryGetValue(state, out int firstCycle))
            {
                burstMask = masks.Skip(firstCycle).Aggregate(0, (value, mask) => value | mask);
                return true; // Same deterministic cooldown state proves indefinite recurrence.
            }
            states.Add(state, cycle);
            int phase = 1, used = 0;
            for (int cast = 0; phase < 4 && cast < 5; cast++)
            {
                int index = -1;
                for (int i = 0; i < team.Length; i++)
                    if ((used & (1 << i)) == 0 && ready[i] <= at && Matches(team[i].Row, phase)) { index = i; break; }
                if (index < 0) return false;
                int next = Next(team[index].Row, phase);
                if (next < phase || next > 4) return false;
                ready[index] = at + cooldowns[index];
                used |= 1 << index;
                phase = next;
            }
            if (phase != 4) return false;
            masks.Add(used);
        }
        return false;
    }
}
