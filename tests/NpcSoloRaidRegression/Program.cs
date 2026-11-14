using System.Diagnostics;
using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Soloraid;
using EpinelPS.Models;
using EpinelPS.Services;
using EpinelPS.Utils;
using Newtonsoft.Json;

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
if (root == null) throw new Exception("Isolated fixture data missing");
var data = new GameData(Directory.GetFiles(Path.Combine(root.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var clock = Path.Combine(AppContext.BaseDirectory, "clock.json");
bool resume = args.Contains("--resume");
var now = resume ? JsonConvert.DeserializeObject<DateTime>(File.ReadAllText(clock)) : DateTime.UtcNow.Date.AddHours(6);
if (!resume) File.WriteAllText(clock, JsonConvert.SerializeObject(now));
var season = new SoloRaidSeasonConfig { RaidId = 1000044, VisibleAt = now.AddDays(-5), StartAt = now.AddDays(-5), EndAt = now.AddDays(10), SettleAt = now.AddDays(10), DisableAt = now.AddDays(17) };
typeof(GameConfig).GetField("_root", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, new GameConfigRoot { SoloRaidAutoSync = false, SoloRaidSeason = season });
SoloRaidSeasonSource.Bind(null);
var manager = data.SoloRaidManagerTable[season.RaidId];
var presets = data.SoloRaidPresetTable.Values.Where(p => p.PresetGroupId == manager.MonsterPreset).OrderBy(p => p.WaveOrder).ToArray();
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "npc-solo-raid-definitions.json"), JsonConvert.SerializeObject(new
{
    Config = data.GameSettings.Where(p => p.Key.Contains("raid", StringComparison.OrdinalIgnoreCase)), Presets = presets,
    Unlock = data.ContentsOpenTable.GetValueOrDefault(ContentsOpen.SoloRaid), Waves = presets.Select(p => data.WaveIntercept001Table[p.Wave]).Distinct()
}, Formatting.Indented));
var json = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var instance = json.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
CoreInfo Empty() => new() { DbVersion = 5, BondRewardLevelsMigrated = true, LauncherTokenKey = new byte[32], EncryptionTokenKey = new byte[32], LogLevel = LogType.Error };
if (!resume) File.WriteAllText(save, JsonConvert.SerializeObject(Empty()));
CoreInfo State() => (CoreInfo)instance.GetValue(null)!;
string Snapshot() => JsonConvert.SerializeObject(State());
T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))!;
void Save() => json.GetMethod("Save")!.Invoke(null, null);
GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "npc-solo-raid.db")}", "sqlite");
using var db = GameContext.CreateNew();
if (resume)
{
    string before = Snapshot(); ManagedNpcService.Tick(now.AddMinutes(1));
    Check(Snapshot() == before, "fresh process replayed committed raid activity");
    Console.WriteLine("PASS separate process retains the raid checkpoint without replay"); return 0;
}
instance.SetValue(null, Empty());
db.Database.EnsureDeleted(); db.Database.EnsureCreated();
ManagedNpcService.CreateBatch(db, 1, "solo-raid-fixture", now);
var template = Copy(State().Users.Single());
template.ManagedNpc!.Seed = 123456; template.SynchroDeviceUpgraded = false; template.SynchroSlots.Clear();
int[] coreNames = [5011, 5065, 5099, 5105, 5103];
var rows = coreNames.Select(n => data.CharacterTable.Values.Where(c => c.NameCode == n && c.IsVisible).OrderBy(c => c.GradeCoreId).First())
    .Concat(data.CharacterTable.Values.Where(c => c.IsVisible && c.OriginalRare == OriginalRareType.SSR && !coreNames.Contains(c.NameCode))
        .GroupBy(c => c.NameCode).Select(g => g.OrderBy(c => c.GradeCoreId).First()).OrderBy(c => c.Id).Take(25)).ToArray();
template.Characters.Clear();
for (int i = 0; i < rows.Length; i++) template.Characters.Add(new CharacterModel
    { Csn = 10000 + i, Tid = rows[i].Id, Level = 200, Grade = 3, Skill1Lvl = 8, Skill2Lvl = 8, UltimateLevel = 8 });
template.RepresentationTeamDataNew = template.Characters.Take(5).Select(c => (long)c.Csn).ToArray();
foreach (var condition in data.ContentsOpenTable[ContentsOpen.SoloRaid].OpenCondition)
{
    if (condition.OpenConditionType != ContentsOpenCondition.StageClear) continue;
    var stage = data.StageDataRecords[condition.OpenConditionValue];
    string map = (string)typeof(GameData).GetMethod("GetMapIdFromChapter", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(data, [stage.ChapterId, stage.ChapterMod])!;
    if (!template.FieldInfoNew.TryGetValue(map, out var field)) template.FieldInfoNew[map] = field = new();
    if (!field.CompletedStages.Contains(stage.Id)) field.CompletedStages.Add(stage.Id);
}
var human = new User { ID = 1, Nickname = "Human", Description = "must remain unchanged", LastReset = now, LastWeeklyReset = now };
db.Users.Add(new GameUser { ID = 1, Nickname = "Human" }); db.SaveChanges();
int passed = 0, failed = 0;
string staticBefore = StaticSnapshot();

Test("completed daily routine enters challenge and the existing ranking endpoint", () =>
{
    Prepare(7); string inventory = Inventory(Npc()); ManagedNpcService.Tick(now);
    Check(Best() is { IsClear: true, IsOpen: false, TotalDamage: > 0 }, "completed routine did not settle challenge");
    Check(Raid().TrialCount == 3, "challenge budget was not consumed exactly");
    var ranking = Ranking();
    Check(ranking.TotalUserCount == 1 && ranking.Rankings.Single().User.Usn == (long)Npc().ID && ranking.Rankings.Single().Damage == Best()!.TotalDamage, "NPC not ranked");
    Check(Inventory(Npc()) == inventory, "owned characters or teams changed");
    Check(JsonConvert.SerializeObject(State().Users.Single(u => u.ID == 1)) == JsonConvert.SerializeObject(human), "human changed");
});
Test("repeated ticks and backward clocks cannot repeat an activity", () =>
{
    Prepare(7); ManagedNpcService.Tick(now); string before = Snapshot();
    ManagedNpcService.Tick(now.AddMinutes(1)); ManagedNpcService.Tick(now.AddMinutes(-1)); Check(Snapshot() == before, "activity replayed");
});
Test("three ordinary attempts per day unlock challenge on the third day", () =>
{
    Prepare(); ManagedNpcService.Tick(now);
    Check(LastClear() == 3 && Raid().RaidOpenCount == 3 && Best() == null, "first day must advance only three ordinary stages");
    CompleteDaily(now.AddDays(1)); ManagedNpcService.Tick(now.AddDays(1));
    Check(LastClear() == 6 && Raid().RaidOpenCount == 3 && Best() == null, "second day did not advance next three stages");
    CompleteDaily(now.AddDays(2)); ManagedNpcService.Tick(now.AddDays(2));
    Check(LastClear() == 7 && Raid().RaidOpenCount == 1 && Best()?.TotalDamage > 0, "third day did not unlock challenge");
});
Test("ordinary clears pay real rewards exactly once", () =>
{
    Prepare(); string before = Resources(Npc()); ManagedNpcService.Tick(now);
    Check(LastClear() > 0 && Resources(Npc()) != before, "clear rewards not granted");
    string after = Resources(Npc()); ManagedNpcService.Tick(now.AddHours(1)); Check(Resources(Npc()) == after && Raid().RaidOpenCount == 3, "rewards repeated");
});
Test("insufficient power stops ordinary progression without a challenge score", () =>
{
    Prepare(); foreach (var c in Npc().Characters) { c.Level = 1; c.Grade = 0; c.Skill1Lvl = c.Skill2Lvl = c.UltimateLevel = 1; }
    ManagedNpcService.Tick(now);
    Check(LastClear() < 7 && Best() == null && Raid().RaidOpenCount is > 0 and <= 3, "weak roster got unearned score");
    Check(Raid().SoloRaidLevels.Any(l => !l.IsClear && l.TotalDamage < l.Hp), "failed battle lacks bounded damage");
});
Test("challenge damage increases with owned skills and cores", () =>
{
    Prepare(7); foreach (var c in Npc().Characters) { c.Grade = 0; c.Skill1Lvl = c.Skill2Lvl = c.UltimateLevel = 1; }
    ManagedNpcService.Tick(now); long low = Best()?.TotalDamage ?? 0;
    Prepare(7); ManagedNpcService.Tick(now); long high = Best()?.TotalDamage ?? 0;
    Check(high > low && low > 0, $"power did not affect score: {low}/{high}");
});
Test("challenge fixed level does not modify ordinary levels", () =>
{
    Prepare(7); ManagedNpcService.Tick(now); long high = Best()?.TotalDamage ?? 0;
    Prepare(7); foreach (var c in Npc().Characters) c.Level = 20;
    string before = Inventory(Npc()); ManagedNpcService.Tick(now);
    Check(Best()?.TotalDamage == high && high > 0, "challenge used ordinary levels");
    Check(Best()!.Logs.SelectMany(l => l.Team).All(c => c.Lv == presets[7].CharacterLv), "wrong fixed-level snapshot");
    Check(Inventory(Npc()) == before, "fixed level leaked to account");
});
Test("partial rosters use only owned unique characters and variants", () =>
{
    Prepare(7); Npc().Characters.RemoveRange(13, Npc().Characters.Count - 13);
    var variant = Copy(Npc().Characters[0]); variant.Csn = 90000; Npc().Characters.Add(variant);
    ManagedNpcService.Tick(now); var teams = Best()?.Logs.SelectMany(l => l.Team).ToArray() ?? [];
    Check(teams.Length == 13 && Best()!.Logs.Count == 3, "small roster was padded dropped or repeated");
    Check(teams.Select(c => c.Csn).Distinct().Count() == teams.Length && teams.Select(c => data.CharacterTable[c.Tid].NameCode).Distinct().Count() == teams.Length, "character reused");
    Check(teams.All(c => Npc().Characters.Any(owned => owned.Csn == c.Csn && owned.Tid == c.Tid)), "unowned character");
});
Test("a worse daily run retains the best damage and original squads", () =>
{
    Prepare(7); var best = new SoloRaidLevelData { RaidLevel = 8, Type = SoloRaidType.Trial, IsClear = true, TotalDamage = 90_000_000_000,
        Logs = [new SoloRaidLogData { Damage = 90_000_000_000, Team = [new TeamCharacterData { Csn = 123, Tid = rows[0].Id, Lv = 400, Combat = 999 }] }] };
    Raid().SoloRaidLevels.Add(best); string before = JsonConvert.SerializeObject(best); ManagedNpcService.Tick(now);
    Check(JsonConvert.SerializeObject(Best()) == before && Raid().TrialCount == 3, "best changed or attempts not consumed");
});
Test("existing normal and trial attempt budgets are honored", () =>
{
    Prepare(); Raid().RaidOpenCount = 3; ManagedNpcService.Tick(now); Check(LastClear() == 0 && Best() == null && Raid().RaidOpenCount == 3, "normal limit bypassed");
    Prepare(7); Raid().TrialCount = 3; ManagedNpcService.Tick(now); Check(Best() == null && Raid().TrialCount == 3, "challenge limit bypassed");
});
Test("paused banned locked and not-yet-due accounts remain untouched", () =>
{
    foreach (string reason in new[] { "manager", "npc", "ban", "locked", "phase" })
    {
        Prepare(7);
        if (reason == "manager") State().NpcManagement!.Enabled = false;
        if (reason == "npc") Npc().ManagedNpc!.Enabled = false;
        if (reason == "ban") Npc().IsBanned = true;
        if (reason == "locked") Npc().FieldInfoNew.Clear();
        if (reason == "phase") { Npc().ManagedNpc!.DailyRoutine!.CompletedPhases = 2; Npc().ManagedNpc.DailyRoutine.NextPhaseUtc = now.AddHours(1); }
        string before = Snapshot(); ManagedNpcService.Tick(now); Check(Snapshot() == before, reason + " account changed");
    }
});
Test("future ended and incomplete seasons cannot consume attempts", () =>
{
    Prepare(); var start = season.StartAt; var end = season.EndAt;
    season.StartAt = now.AddHours(1); string before = Snapshot(); ManagedNpcService.Tick(now); Check(Snapshot() == before, "future season ran");
    season.StartAt = start; season.EndAt = now; before = Snapshot(); ManagedNpcService.Tick(now); Check(Snapshot() == before, "ended season ran");
    season.EndAt = end; var missing = presets[3]; data.SoloRaidPresetTable.Remove(missing.Id);
    try { before = Snapshot(); ManagedNpcService.Tick(now); Check(Snapshot() == before, "incomplete season consumed attempts"); }
    finally { data.SoloRaidPresetTable[missing.Id] = missing; }
});
Test("season opening after daily completion runs without replaying phases", () =>
{
    Prepare(7); var start = season.StartAt;
    try
    {
        season.StartAt = now.AddMinutes(1); ManagedNpcService.Tick(now); Check(Best() == null, "season ran early");
        ManagedNpcService.Tick(now.AddMinutes(2));
        Check(Best()?.TotalDamage > 0 && Npc().ManagedNpc!.DailyRoutine!.CompletedPhases == 5, "new season deferred until tomorrow");
    }
    finally { season.StartAt = start; }
});
Test("same-day season rollover retains history and admits the new season", () =>
{
    Prepare(7); ManagedNpcService.Tick(now); string old = JsonConvert.SerializeObject(Raid()); int oldId = season.RaidId;
    int nextId = data.SoloRaidManagerTable.Keys.Where(id => id != oldId).Max(); season.RaidId = nextId;
    try
    {
        SeedNormal(Npc(), nextId, 7); ManagedNpcService.Tick(now.AddMinutes(1));
        Check(JsonConvert.SerializeObject(Npc().SoloRaidData[oldId]) == old, "old season changed");
        Check(Npc().SoloRaidData[nextId].SoloRaidLevels.Any(l => l.Type == SoloRaidType.Trial && l.TotalDamage > 0), "new season blocked");
    }
    finally { season.RaidId = oldId; }
});
Test("failed save rolls back rewards scores and checkpoint and permits retry", () =>
{
    Prepare(); string before = Snapshot(); Directory.CreateDirectory(save + ".tmp"); bool rejected = false;
    try { ManagedNpcService.Tick(now); }
    catch (IOException) { rejected = true; }
    catch (UnauthorizedAccessException) { rejected = true; }
    finally { Directory.Delete(save + ".tmp"); }
    Check(rejected && Snapshot() == before, "save failure partly committed battle");
    ManagedNpcService.Tick(now); Check(LastClear() == 3, "rollback could not retry");
});
Test("explicit-level CP reuses the formula without mutating the user", () =>
{
    Prepare(); var npc = Npc(); string before = Inventory(npc);
    var method = typeof(FormulaUtils).GetMethod("CalculateCP", [typeof(User), typeof(long), typeof(int?)]);
    Check(method != null, "explicit-level CP overload missing");
    int actual = (int)method!.Invoke(null, [npc, (long)npc.Characters[0].Csn, 400])!;
    var other = Copy(npc); other.Characters[0].Level = 400;
    Check(actual == FormulaUtils.CalculateCP(other, other.Characters[0].Csn) && actual > 0 && Inventory(npc) == before, "fixed-level formula differs or mutates account");
});
Test("completed populations share one season validation per worker tick", () =>
{
    Prepare(7); ManagedNpcService.Tick(now); var done = Copy(Npc());
    int Reads(int count)
    {
        var core = Empty(); core.NpcManagement = new();
        for (int i = 0; i < count; i++) { var npc = Copy(done); npc.ID += (ulong)i; core.Users.Add(npc); }
        instance.SetValue(null, core); int reads = 0;
        SoloRaidSeasonSource.Bind(new SoloRaidSeasonCatalog(() => { reads++; return data; }, Path.Combine(AppContext.BaseDirectory, "unused-season-cache.json")));
        ManagedNpcService.Tick(now.AddMinutes(1)); return reads;
    }
    try
    {
        GameConfig.Root.SoloRaidAutoSync = true;
        int one = Reads(1), many = Reads(50);
        Check(one > 0 && many <= one + 1, $"completed NPCs repeated season validation: one={one}, fifty={many}");
    }
    finally { SoloRaidSeasonSource.Bind(null); GameConfig.Root.SoloRaidAutoSync = false; }
});
Test("partial SR squads receive valid cadence and 60s rotation beats no rotation", () =>
{
    int[] names = [5011, 5065, 5105];
    var members = names.Select(n => rows.Single(r => r.NameCode == n)).ToArray();
    var rarity = members.Select(r => r.OriginalRare).ToArray();
    var skills = members.Select(r => data.CharacterSkills[r.UltiSkillId]).ToArray();
    var cooldowns = skills.Select(s => s.SkillCooltime).ToArray();
    long Score(int cooldown)
    {
        Prepare(7); Npc().Characters.RemoveAll(c => !names.Contains(data.CharacterTable[c.Tid].NameCode));
        foreach (var row in members) row.OriginalRare = OriginalRareType.SR;
        foreach (var skill in skills) skill.SkillCooltime = cooldown;
        ManagedNpcService.Tick(now); return Best()?.TotalDamage ?? 0;
    }
    try
    {
        long fast = Score(2000), slow = Score(6000), broken = Score(6100);
        Check(fast > slow && slow > broken && broken > 0, $"cadence is not valid or monotonic: 20s={fast}, 60s={slow}, broken={broken}");
    }
    finally
    {
        for (int i = 0; i < members.Length; i++) { members[i].OriginalRare = rarity[i]; skills[i].SkillCooltime = cooldowns[i]; }
    }
});
Test("malformed challenge mode fixed level and secondary boss stats are rejected", () =>
{
    var trial = presets[7]; var mode = trial.DifficultyType; int level = trial.CharacterLv;
    var wave = data.WaveIntercept001Table[trial.Wave]; var targets = wave.TargetList.ToList();
    void Untouched()
    {
        Prepare(); string before = Snapshot(); ManagedNpcService.Tick(now);
        Check(Snapshot() == before, "invalid challenge data consumed attempts or rewards");
    }
    try
    {
        trial.DifficultyType = DifficultyType.Common; Untouched(); trial.DifficultyType = mode;
        trial.CharacterLv = 0; Untouched(); trial.CharacterLv = -1; Untouched(); trial.CharacterLv = level;
        long missingStats = data.MonsterTable.Values.First(m => !data.MonsterStatEnhanceTable.Values.Any(s => s.GroupId == m.StatenhanceId && s.Lv == trial.MonsterStageLv && s.LevelHp > 0)).Id;
        wave.TargetList.Add(missingStats); Untouched();
    }
    finally { trial.DifficultyType = mode; trial.CharacterLv = level; wave.TargetList = targets; }
});
await AsyncTest("committed raid checkpoint survives an independent process restart", async () =>
{
    Prepare(7); ManagedNpcService.Tick(now); Check(Best()?.TotalDamage > 0, "no raid record to restart"); Save();
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--resume") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory })!;
    await child.WaitForExitAsync(); Check(child.ExitCode == 0, "restart verification failed");
});
Test("activities never mutate shared character monster wave or reward tables", () => Check(StaticSnapshot() == staticBefore, "static tables changed"));
if (failed == 0)
{
    var population = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(Path.Combine(root.FullName, "Server and Server selector/db.json")))!;
    var core = Empty(); core.Users = population.Users.Where(u => u.ManagedNpc != null).Select(u => Copy(u)).ToList(); core.NpcManagement = new(); instance.SetValue(null, core);
    foreach (var npc in core.Users) { npc.SoloRaidData.Clear(); npc.ManagedNpc!.LastActionUtc = now.AddMinutes(-1); }
    var days = new List<object>();
    for (int day = 0; day < 3; day++)
    {
        DateTime at = now.AddDays(day); foreach (var npc in State().Users) CompleteRoutine(npc, at); ManagedNpcService.Tick(at);
        days.Add(new { Day = day + 1, Participants = State().Users.Count(u => u.SoloRaidData.GetValueOrDefault(season.RaidId)?.SoloRaidLevels.Any(l => l.Type == SoloRaidType.Trial && l.TotalDamage > 0) == true),
            Clears = State().Users.GroupBy(u => u.SoloRaidData.GetValueOrDefault(season.RaidId)?.SoloRaidLevels.Where(l => l.Type == SoloRaidType.Normal && l.IsClear).Select(l => l.RaidLevel).DefaultIfEmpty(0).Max() ?? 0).ToDictionary(g => g.Key, g => g.Count()) });
    }
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "population-simulation.json"), JsonConvert.SerializeObject(new { FixtureNpcCount = core.Users.Count, LiveSaveUsed = false, Days = days }, Formatting.Indented));
    Console.WriteLine("PASS isolated population simulation: " + JsonConvert.SerializeObject(days));
}
Console.WriteLine($"{passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
async Task AsyncTest(string name, Func<Task> action) { try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
User Npc() => State().Users.Single(u => u.ManagedNpc != null);
SoloRaidInfo Raid() => Npc().SoloRaidData[season.RaidId];
SoloRaidLevelData? Best() => Raid().SoloRaidLevels.Where(l => l.Type == SoloRaidType.Trial && !l.IsOpen && l.IsClear).MaxBy(l => l.TotalDamage);
int LastClear() => Raid().SoloRaidLevels.Where(l => l.Type == SoloRaidType.Normal && l.IsClear).Select(l => l.RaidLevel).DefaultIfEmpty(0).Max();
string Inventory(User u) => JsonConvert.SerializeObject(new { u.Characters, u.UserTeams, u.RepresentationTeamDataNew, u.SynchroDeviceLevel, u.SynchroDeviceUpgraded, u.SynchroSlots });
string Resources(User u) => JsonConvert.SerializeObject(new { Currency = u.Currency.OrderBy(p => p.Key), Items = u.Items.GroupBy(i => i.ItemType).OrderBy(g => g.Key).Select(g => new { Id = g.Key, Count = g.Sum(i => i.Count) }) });
string StaticSnapshot() => JsonConvert.SerializeObject(new { data.CharacterTable, data.MonsterTable, data.MonsterStatEnhanceTable, data.WaveIntercept001Table, data.RewardDataRecords });
ResGetSoloRaidRanking Ranking() { var result = new ResGetSoloRaidRanking(); SoloRaidHelper.GetSoloRaidRanking(State().Users.Single(u => u.ID == 1), ref result); return result; }
void SeedNormal(User npc, int raidId, int count)
{
    var raid = new SoloRaidInfo { RaidId = raidId, LastDateDay = int.Parse(now.AddHours(4).ToString("yyyyMMdd")) };
    for (int level = 1; level <= count; level++) raid.SoloRaidLevels.Add(new SoloRaidLevelData { RaidLevel = level, Type = SoloRaidType.Normal, IsClear = true });
    npc.SoloRaidData[raidId] = raid;
}
void CompleteRoutine(User npc, DateTime at) => npc.ManagedNpc!.DailyRoutine = new NpcDailyRoutineState
    { Day = int.Parse(at.AddHours(-State().ResetHourUtcTime).ToString("yyyyMMdd")), CompletedPhases = 5, StartedUtc = at.AddMinutes(-30), LastCompletedUtc = at.AddMinutes(-5), NextPhaseUtc = at.AddHours(1) };
void CompleteDaily(DateTime at) => CompleteRoutine(Npc(), at);
void Prepare(int clears = 0)
{
    var core = Empty(); core.Users = [Copy(human), Copy(template)]; core.NpcManagement = new(); instance.SetValue(null, core); Logging.SetOutputLevel(LogType.Error);
    Npc().ManagedNpc!.LastActionUtc = now.AddMinutes(-5); CompleteDaily(now); SeedNormal(Npc(), season.RaidId, clears);
}
