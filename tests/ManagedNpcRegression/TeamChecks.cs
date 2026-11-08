using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using Newtonsoft.Json;

internal static class TeamChecks
{
    private static readonly Assembly Assembly = typeof(GameContext).Assembly;
    private static Type Teams => Assembly.GetType("EpinelPS.Services.ManagedNpcTeams")
        ?? throw new Exception("FAIL: owned-SSR burst-aware team selection is unavailable");
    private static Type Roster => Assembly.GetType("EpinelPS.Services.ManagedNpcRoster")!;
    private static Type Service => Assembly.GetType("EpinelPS.Services.ManagedNpcService")!;

    public static int VerifySaved(GameData data)
    {
        var jsonDb = Assembly.GetType("EpinelPS.Database.JsonDb")!;
        var instance = jsonDb.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
        var core = (CoreInfo)instance.GetValue(null)!;
        var npcs = core.Users.Where(u => u.ManagedNpc != null).ToArray();
        Check(npcs.Length == 100 && npcs.All(u => (int)u.ManagedNpc!.GetType().GetProperty("TeamSchemaVersion")!.GetValue(u.ManagedNpc)! == 1),
            "restart must preserve all 100 migrated NPCs");
        GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "npc-team-regression.db")}", "sqlite");
        using var db = GameContext.CreateNew();
        Check(db.Users.Count() == 101 && !db.SdkUsers.Any() && npcs.All(u => db.Users.Any(p => p.ID == u.ID)), "restart profiles must retain NPC identities without login accounts");
        string before = JsonConvert.SerializeObject(core);
        Call(Service, "UpgradeRosters", DateTime.UtcNow);
        Check((int)Call(Service, "EnsurePopulation", db, 100, DateTime.UtcNow)! == 0 && JsonConvert.SerializeObject(instance.GetValue(null)) == before,
            "restart must neither remigrate nor re-expand NPCs");
        Check(npcs.All(u => (bool)Call(Teams, "CanCycle", u, u.RepresentationTeamDataNew, 20)!), "restarted fixture squads retain sustainable 20-second cycles");
        Console.WriteLine("PASS: separate-process restart preserves 100 identities, team migration, burst cycles and target-count idempotence");
        return 0;
    }

    public static int AuditLive(string root, GameData data)
    {
        // Deserialize a separate copy. Never initialize JsonDb or save the live database.
        var core = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(Path.Combine(root, "Server and Server selector/db.json")))!;
        var npcs = core.Users.Where(u => u.ManagedNpc != null).ToArray();
        Check(npcs.Length == 100, "live population must contain exactly 100 NPCs");
        var rows = new List<object>();
        foreach (var npc in npcs)
        {
            var state = npc.ManagedNpc!;
            int ssrs = (int)Call(Roster, "SsrCount", npc)!;
            int tier = (int)Call(Roster, "TierForCount", ssrs)!;
            int cadence = (int)state.GetType().GetProperty("BurstCycleSeconds")!.GetValue(state)!;
            Check((bool)Call(Teams, "CanCycle", npc, npc.RepresentationTeamDataNew, cadence)!, "stored live team must sustain its cadence: " + state.Name);
            Check((int)state.GetType().GetProperty("RosterTier")!.GetValue(state)! == tier &&
                (int)state.GetType().GetProperty("TeamSchemaVersion")!.GetValue(state)! == 1, "live tier and migration metadata must agree");
            Check(((System.Collections.ICollection)state.GetType().GetProperty("TeamSynergies")!.GetValue(state)!).Count > 0,
                "live main team must have an evidenced skill interaction");
            int[] minimum = [2, 4, 6, 8, 9];
            Check(npc.Characters.Where(c => npc.RepresentationTeamDataNew.Contains(c.Csn)).All(c =>
                new[] { c.Skill1Lvl, c.Skill2Lvl, c.UltimateLevel }.All(v => v >= minimum[tier] && v <= 10)), "live main skills must meet their tier without exceeding ten");
            foreach (int type in new[] { 1, 3, 4, 12, 13 })
            {
                if (!npc.UserTeams.TryGetValue(type, out var teams)) continue;
                Check(teams.Teams.First(t => t.TeamNumber == 1).Slots.OrderBy(s => s.Slot).Select(s => s.Value).SequenceEqual(npc.RepresentationTeamDataNew), "live profile/campaign/arena mains must agree");
                if (type is 12 or 13)
                    Check(teams.Teams.SelectMany(t => t.Slots).Select(s => data.CharacterTable[npc.Characters.Single(c => c.Csn == s.Value).Tid].NameCode).Distinct().Count() == 15, "live special arena squads must be disjoint");
            }
            rows.Add(new { npc.ID, state.Name, RosterTier = tier + 1, SsrCount = ssrs,
                BurstCycleSeconds = cadence, Synergies = state.GetType().GetProperty("TeamSynergies")!.GetValue(state),
                Main = npc.RepresentationTeamDataNew.Select(csn => npc.Characters.Single(c => c.Csn == csn)).ToArray() });
        }
        File.WriteAllText(Path.Combine(root, ".tools/npc-teams-20261107/installed-rosters.json"), JsonConvert.SerializeObject(rows, Formatting.Indented));
        Console.WriteLine("PASS: 100 installed SSR teams, recurring burst cadence, collection tiers and synchronized arena squads");
        return 0;
    }

    public static int Run(string root, GameData data)
    {
        _ = Teams;
        User Owned(params int[] names)
        {
            var user = new User { ID = 1500000000, ManagedNpc = new() { Name = "配队验证", Seed = 12345 } };
            foreach (int name in names)
            {
                var row = data.CharacterTable.Values.Where(c => c.NameCode == name && c.IsVisible)
                    .OrderBy(c => c.GradeCoreId).ThenBy(c => c.Id).First();
                user.Characters.Add(new CharacterModel { Csn = 10001 + user.Characters.Count, Tid = row.Id,
                    Level = 100, Skill1Lvl = 1, Skill2Lvl = 1, UltimateLevel = 1 });
            }
            return user;
        }
        long[] Select(User user)
        {
            var plan = Call(Teams, "Select", user)!;
            return (long[])plan.GetType().GetProperty("SerialNumbers")!.GetValue(plan)!;
        }
        bool Cycle(User user, int seconds) => (bool)Call(Teams, "CanCycle", user,
            user.Characters.Select(c => (long)c.Csn).ToArray(), seconds)!;
        int[] Names(User user, long[] selected) => selected.Select(csn => data.CharacterTable[user.Characters.Single(c => c.Csn == csn).Tid].NameCode).ToArray();

        var basic = Owned(5011, 5036, 5024, 5001, 5066, 1016);
        string original = JsonConvert.SerializeObject(basic);
        var selected = Select(basic);
        Check(selected.Length == 5 && selected.Distinct().Count() == 5 && selected.All(csn =>
            data.CharacterTable[basic.Characters.Single(c => c.Csn == csn).Tid].OriginalRare == OriginalRareType.SSR), "five distinct owned SSRs");
        Check(JsonConvert.SerializeObject(basic) == original && selected.SequenceEqual(Select(basic)), "selection is pure and repeatable");

        var reentry = Owned(5100, 5099, 5024, 5001, 5066);
        Check(!Cycle(reentry, 20) && !Cycle(reentry, 60), "a sole re-entry stage I cannot advance the burst chain");
        var reentry2 = Owned(5011, 5157, 5024, 5001, 5066);
        Check(!Cycle(reentry2, 20), "a sole re-entry stage II cannot advance the burst chain");
        var slow = Owned(5005, 5036, 5024, 5001, 5066);
        Check(!Cycle(slow, 20) && Cycle(slow, 40), "40-second stage I must not be called a 20-second cycle");
        var longCooldown = Owned(5011, 5008, 5009, 5001, 5066);
        Check(!Cycle(longCooldown, 20) && Cycle(longCooldown, 60), "conditional Blanc cooldown reduction is not assumed");
        var allStage = Owned(5011, 5036, 5101, 5066, 5009);
        Check(Cycle(allStage, 20), "all-stage Red Hood can alternate stage III with a regular stage III");
        var insufficient = Owned(5101, 5036, 5030, 5065, 5099);
        Check(!Cycle(insufficient, 20), "all-stage characters cannot be used twice in one burst chain or ignore their cooldown");
        Console.WriteLine("PASS: owned SSR selection, purity, re-entry, all-stage and repeating cooldown constraints");

        var pair = Owned(5011, 5100, 5099, 5024, 5001, 5023);
        var picked = Names(pair, Select(pair));
        Check(picked.Contains(5100) && picked.Contains(5099), "owned Tia and Naga should be selected together over neutral filler");
        var damagePair = Owned(5011, 5116, 5117, 5024, 5001, 5023);
        var damagePicked = Names(damagePair, Select(damagePair));
        Check(damagePicked.Contains(5116) && damagePicked.Contains(5117), "summer Rosanna and summer Sakura skill synergy should be retained");
        var rockets = Owned(5011, 5108, 5090, 5105, 5099, 5036);
        Check(Names(rockets, Select(rockets)).Contains(5108), "Rem's launcher synergy should be recognized for owned launcher attackers");
        var inactiveAttack = Call(Teams, "Select", Owned(5104, 5049, 5021, 5094, 5024))!;
        var inactiveAttackLabels = (string[])inactiveAttack.GetType().GetProperty("Synergies")!.GetValue(inactiveAttack)!;
        var shadowed = Call(Teams, "Select", Owned(5081, 5008, 5167, 5009, 5024))!;
        var shadowedLabels = (string[])shadowed.GetType().GetProperty("Synergies")!.GetValue(shadowed)!;
        Console.WriteLine($"REVIEW CHECK: inactive Mica attack credited={inactiveAttackLabels.Contains("攻击辅助与输出配合")}; Blanc-shadowed Arcana credited={shadowedLabels.Contains("命运伴侣：霰弹枪攻击增益")}");
        Check(!inactiveAttackLabels.Contains("攻击辅助与输出配合"), "a permanently shadowed Mica burst cannot contribute an attack-buff label");
        Check(!shadowedLabels.Contains("命运伴侣：霰弹枪攻击增益"), "Blanc's guaranteed 777 cooldown reduction must not invent backup Arcana burst buffs");
        Console.WriteLine("PASS: known skill interactions affect actual selected teams");

        foreach (var boundary in new[] { (5, 0), (12, 0), (13, 1), (18, 1), (19, 2), (24, 2), (25, 3), (30, 3), (31, 4), (100, 4) })
            Check((int)Call(Roster, "TierForCount", boundary.Item1)! == boundary.Item2, "collection tier boundary " + boundary.Item1);
        var actual = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(Path.Combine(root, "Server and Server selector/db.json")))!;
        var current = actual.Users.Where(u => u.ManagedNpc != null).ToArray();
        foreach (var npc in current)
        {
            var ownership = npc.Characters.Select(c => (c.Csn, c.Tid, c.Level, c.Grade)).ToArray();
            Call(Roster, "Apply", npc);
            Check(ownership.SequenceEqual(npc.Characters.Select(c => (c.Csn, c.Tid, c.Level, c.Grade))), "rebuild must preserve owned cards and levels: " + npc.ManagedNpc!.Name);
            var team = npc.RepresentationTeamDataNew;
            Check(team.Length == 5 && team.Distinct().Count() == 5 && team.All(csn => data.CharacterTable[npc.Characters.Single(c => c.Csn == csn).Tid].OriginalRare == OriginalRareType.SSR), "actual inventory must yield a five-SSR team");
            int cadence = (int)npc.ManagedNpc!.GetType().GetProperty("BurstCycleSeconds")!.GetValue(npc.ManagedNpc)!;
            Check((bool)Call(Teams, "CanCycle", npc, team, cadence)!, "actual selected team must sustain its advertised cadence");
            Check(cadence == (npc.ID is 1500000015UL or 1500000021UL ? 40 : 20), "use the shortest feasible current-inventory cadence: " + npc.ManagedNpc.Name);
            Check(((System.Collections.ICollection)npc.ManagedNpc.GetType().GetProperty("TeamSynergies")!.GetValue(npc.ManagedNpc)!).Count > 0,
                "actual owned inventory should use an evidenced skill interaction: " + npc.ManagedNpc.Name);
            var campaign = npc.UserTeams[1].Teams.Single(t => t.TeamNumber == 1).Slots.OrderBy(s => s.Slot).Select(s => s.Value);
            Check(campaign.SequenceEqual(team), "campaign and profile squads must agree");
            foreach (int type in new[] { 3, 4, 12, 13 })
            {
                if (!npc.UserTeams.TryGetValue(type, out var arena)) continue;
                Check(arena.Teams.First(t => t.TeamNumber == 1).Slots.OrderBy(s => s.Slot).Select(s => s.Value).SequenceEqual(team), "NPC arena main must agree with profile");
                if (type is 12 or 13)
                    Check(arena.Teams.SelectMany(t => t.Slots).Select(s => data.CharacterTable[npc.Characters.Single(c => c.Csn == s.Value).Tid].NameCode).Distinct().Count() == 15,
                        "special arena rounds must not repeat characters");
            }
        }
        string previewPath = Path.Combine(root, ".tools/npc-teams-20261107/preview-existing.json");
        Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
        File.WriteAllText(previewPath, JsonConvert.SerializeObject(current.Select(npc => new
        {
            npc.ID, npc.ManagedNpc, SsrCount = npc.Characters.Count(c => data.CharacterTable[c.Tid].OriginalRare == OriginalRareType.SSR),
            Main = npc.RepresentationTeamDataNew.Select(csn => npc.Characters.Single(c => c.Csn == csn))
        }), Formatting.Indented));
        Console.WriteLine($"PASS: {current.Length} real owned inventories retain cards/levels and gain sustainable SSR teams");

        var jsonDb = Assembly.GetType("EpinelPS.Database.JsonDb")!;
        var instance = jsonDb.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
        var human = new User { ID = 1, LastNormalStageCleared = 6000002, Characters = [new() { Csn = 900, Tid = 201001, Level = 12 }] };
        var fixture = new CoreInfo { Users = [human], BondRewardLevelsMigrated = true };
        instance.SetValue(null, fixture);
        GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "npc-team-regression.db")}", "sqlite");
        using var db = GameContext.CreateNew();
        db.Database.EnsureDeleted(); db.Database.EnsureCreated(); db.Users.Add(new GameUser { ID = 1, Nickname = "Human" }); db.SaveChanges();
        var now = new DateTime(2026, 11, 7, 2, 0, 0, DateTimeKind.Utc);
        string humanBefore = JsonConvert.SerializeObject(human);
        Check((int)Call(Service, "EnsurePopulation", db, 100, now)! == 100, "empty population expands to exactly 100");
        CoreInfo Get() => (CoreInfo)instance.GetValue(null)!;
        Check(Get().Users.Count(u => u.ManagedNpc != null) == 100 && db.Users.Count() == 101 && !db.SdkUsers.Any(), "100 independent NPC profiles without login accounts");
        Check(JsonConvert.SerializeObject(Get().Users.Single(u => u.ID == 1)) == humanBefore, "expansion must preserve human save");
        Check((int)Call(Service, "EnsurePopulation", db, 100, now)! == 0 && Get().Users.Count == 101, "target-count retries cannot add another 100");
        var counts = new int[5];
        foreach (var npc in Get().Users.Where(u => u.ManagedNpc != null))
        {
            int ssrs = npc.Characters.Count(c => data.CharacterTable[c.Tid].OriginalRare == OriginalRareType.SSR);
            int tier = (int)Call(Roster, "TierForCount", ssrs)!; counts[tier]++;
            Check((bool)Call(Teams, "CanCycle", npc, npc.RepresentationTeamDataNew, 20)!, "new NPC core must sustain a 20-second cycle");
            Check(((System.Collections.ICollection)npc.ManagedNpc!.GetType().GetProperty("TeamSynergies")!.GetValue(npc.ManagedNpc)!).Count > 0,
                "new NPC inventory must support an explicit skill interaction");
            int[] low = [2, 4, 6, 8, 9], high = [3, 5, 7, 9, 10];
            foreach (var c in npc.Characters.Where(c => npc.RepresentationTeamDataNew.Contains(c.Csn)))
                Check(new[] { c.Skill1Lvl, c.Skill2Lvl, c.UltimateLevel }.All(v => v >= low[tier] && v <= high[tier]), "new main skills must follow inventory tier");
        }
        Check(counts.All(c => c == 20), "new 100-player population covers five collection tiers evenly");
        Call(Service, "UpgradeRosters", now);
        string migrated = JsonConvert.SerializeObject(Get());
        Call(Service, "UpgradeRosters", now.AddDays(1));
        Check(JsonConvert.SerializeObject(Get()) == migrated, "team migration must be idempotent");
        Check(JsonConvert.SerializeObject(Get().Users.Single(u => u.ID == 1)) == humanBefore, "migration must preserve human characters and save");
        foreach (var npc in Get().Users.Where(u => u.ManagedNpc != null)) npc.ManagedNpc!.GetType().GetProperty("TeamSchemaVersion")!.SetValue(npc.ManagedNpc, 0);
        string beforeFailure = JsonConvert.SerializeObject(Get());
        string pendingPath = Path.Combine(AppContext.BaseDirectory, "db.json.tmp");
        Directory.CreateDirectory(pendingPath);
        bool failed = false;
        try { Call(Service, "UpgradeRosters", now); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
        finally { Directory.Delete(pendingPath); }
        Check(failed && JsonConvert.SerializeObject(Get()) == beforeFailure, "failed migration save must roll back team and skills");
        Console.WriteLine("PASS: five collection/skill tiers, 100-player target retries, human preservation, migration idempotence and save rollback");

        foreach (var npc in Get().Users.Where(u => u.ManagedNpc != null)) npc.ManagedNpc!.Enabled = false;
        var training = Get().Users.First(u => u.ManagedNpc != null);
        training.Characters = Owned(5011, 5036, 5024, 5001, 5066, 5108, 5099, 5004, 5105, 5007, 5008, 5009).Characters;
        Call(Roster, "Apply", training);
        training.Characters = training.Characters.OrderBy(c => training.RepresentationTeamDataNew.Contains(c.Csn)).ToList();
        foreach (var c in training.Characters) c.Skill1Lvl = c.Skill2Lvl = c.UltimateLevel = 1;
        var state = training.ManagedNpc!;
        state.Enabled = true;
        state.LastActionUtc = now.AddMinutes(-1);
        state.Day = int.Parse(now.AddHours(-Get().ResetHourUtcTime).ToString("yyyyMMdd"));
        state.DailyRoutine = new NpcDailyRoutineState { Day = state.Day, StartedUtc = now.AddMinutes(-6), NextPhaseUtc = now, CompletedPhases = 1 };
        training.LastReset = training.LastWeeklyReset = now;
        training.Currency[CurrencyType.Gold] = training.Currency[CurrencyType.CharacterExp] = training.Currency[CurrencyType.CharacterExp2] = 1_000_000_000;
        long itemSerial = 900000;
        foreach (var c in training.Characters.Where(c => training.RepresentationTeamDataNew.Contains(c.Csn)))
        {
            var row = data.CharacterTable[c.Tid];
            foreach (int skillId in new[] { row.Skill1Id, row.Skill2Id, row.UltiSkillId })
            {
                if (!data.skillInfoTable.TryGetValue(skillId, out var info) || !data.costTable.TryGetValue(info.LevelUpCostId, out var cost)) continue;
                foreach (var item in cost.Costs.Where(i => i.ItemType == RewardType.Item && i.ItemValue > 0))
                    if (!training.Items.Any(i => i.ItemType == item.ItemId)) training.Items.Add(new DbItemData { Isn = itemSerial++, ItemType = item.ItemId, Count = 1_000_000 });
            }
        }
        Call(Service, "Tick", now);
        training = Get().Users.Single(u => u.ID == training.ID);
        var trained = training.Characters.Where(c => c.Skill1Lvl > 1 || c.Skill2Lvl > 1 || c.UltimateLevel > 1).ToArray();
        Check(trained.Length is > 0 and <= 3 && trained.All(c => training.RepresentationTeamDataNew.Contains(c.Csn)),
            "paid training follows selected serial numbers after roster reordering");
        string skillSnapshot = JsonConvert.SerializeObject(training.Characters.Select(c => (c.Csn, c.Skill1Lvl, c.Skill2Lvl, c.UltimateLevel)));
        string currencySnapshot = JsonConvert.SerializeObject(training.Currency);
        Call(Service, "Tick", now.AddMinutes(1));
        Check(JsonConvert.SerializeObject(training.Characters.Select(c => (c.Csn, c.Skill1Lvl, c.Skill2Lvl, c.UltimateLevel))) == skillSnapshot &&
            JsonConvert.SerializeObject(training.Currency) == currencySnapshot, "repeated same-day ticks cannot repeat paid training");
        var previousCards = training.Characters.Select(c => (c.Csn, c.Tid)).ToArray();
        training.Currency[CurrencyType.CharPremiumTicket] = 1;
        int previousPulls = training.GetGachaCountForType(GachaPremiumType.GachaPremium);
        training.ManagedNpc!.DailyRoutine!.NextPhaseUtc = now.AddMinutes(2);
        Call(Service, "Tick", now.AddMinutes(2));
        training = Get().Users.Single(u => u.ID == training.ID);
        Check(training.GetCurrencyVal(CurrencyType.CharPremiumTicket) == 0 && training.GetGachaCountForType(GachaPremiumType.GachaPremium) == previousPulls + 1 &&
            previousCards.All(card => training.Characters.Any(c => c.Csn == card.Csn && c.Tid == card.Tid)),
            "recruiting spends the real ticket and preserves all existing cards");
        Check((int)training.ManagedNpc!.GetType().GetProperty("RosterTier")!.GetValue(training.ManagedNpc)! ==
            (int)Call(Roster, "TierForCount", (int)Call(Roster, "SsrCount", training)!)!, "collection tier tracks actual SSR ownership");
        Check(JsonConvert.SerializeObject(training.Characters.Where(c => previousCards.Any(p => p.Csn == c.Csn))
            .Select(c => (c.Csn, c.Skill1Lvl, c.Skill2Lvl, c.UltimateLevel))) == skillSnapshot,
            "recruiting and team refresh do not grant free skill levels");
        Call(Service, "UpgradeRosters", now.AddMinutes(2));
        Check(JsonConvert.SerializeObject(Get().Users.Single(u => u.ID == 1)) == humanBefore, "ongoing NPC training and recruiting preserve the human save");
        Console.WriteLine("PASS: selected-CSN paid training, once-per-day phases, real recruiting, no free tier skills and preserved human state");
        return 0;
    }

    private static object? Call(Type type, string method, params object[] args)
    {
        try { return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
}
