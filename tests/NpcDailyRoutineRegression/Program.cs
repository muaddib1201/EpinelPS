using System.Diagnostics;
using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.EntityFrameworkCore;

try
{
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
    if (root == null) throw new Exception("Isolated fixture data missing");
    var pack = Directory.GetFiles(Path.Combine(root.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
    var data = new GameData(pack);
    typeof(GameData).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, data);
    await GameDataInitializer.InitializeGameData(new Progress<double>());
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "routine-definitions.json"), JsonConvert.SerializeObject(new
    {
        Contents = data.ContentsOpenTable, Missions = data.TriggerTable.Values.Where(t => t.HandlerType is HandlerType.MissionDaily or HandlerType.MissionWeekly),
        DispatchBoards = data.DispatchBoardTable, Dispatch = data.DispatchTable.Values.Take(20), Simulation = data.SimulationRoomDifficultyTable,
        EarlyQuests = data.QuestDataRecords.Values.Where(q => q.Id <= 40), OutpostLevels = data.OutpostBattle.Keys.Take(25)
    }, Formatting.Indented));
    var json = typeof(GameContext).Assembly.GetType("EpinelPS.Database.JsonDb")!;
    var instance = json.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
    var path = Path.Combine(AppContext.BaseDirectory, "db.json");
    GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "npc-daily-regression.db")}", "sqlite");
    var now = new DateTime(2026, 11, 8, 5, 0, 0, DateTimeKind.Utc);
    CoreInfo State() => (CoreInfo)instance.GetValue(null)!;
    User Npc() => State().Users.Single(u => u.ID == 1500000000);
    JObject Routine() => JObject.FromObject(Npc().ManagedNpc!)["DailyRoutine"] as JObject
        ?? throw new Exception("FAIL: NPC daily routine state is unavailable");
    int Completed() => (int)Routine()["CompletedPhases"]!;
    string Snapshot() => JsonConvert.SerializeObject(State());
    void Save() => json.GetMethod("Save")!.Invoke(null, null);
    if (args.Contains("--resume"))
    {
        Check(Completed() == 2, "restart reads exactly two completed phases");
        var saved = Snapshot();
        ManagedNpcService.Tick(now.AddMinutes(12));
        Check(Snapshot() == saved, "restart at the committed timestamp must not repeat resources or actions");
        ManagedNpcService.Tick(now.AddMinutes(30));
        Check(Completed() == 5, "restart resumes only the remaining three phases");
        Check(Npc().GetCurrencyVal(CurrencyType.CharPremiumTicket) == 0 && Npc().GetGachaCountForType(GachaPremiumType.GachaPremium) == 1,
            "recruiting consumes the owned ticket and records the actual pull");
        Check(JsonConvert.SerializeObject(data.RewardDataRecords) == File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reward-tables-before.json")),
            "multi-chapter simulation rewards never mutate shared static tables");
        Check(Npc().ResetableData.DailyMissionPoints >= 100 && Npc().ResetableData.CompletedDailyMissions.Contains(10017),
            "real action progress claims daily points and the unlocked hundred-point chest");
        int day = (int)Routine()["Day"]!;
        Check(Npc().Guild.ActivityByDay!.GetValueOrDefault(day) == 100 && Npc().GuildContributionByDay.GetValueOrDefault(day) == 100 &&
            State().Guilds[Npc().Guild.guildId!.Value].Data.ActivityPoint == 100,
            "new daily task points credit the remaining union budget without duplicating thirty legacy points");
        Check(Npc().Arena?.RookieCount == 5 && Npc().Arena.RookieRecords.Count >= 5 && Npc().GetCurrencyVal(CurrencyType.ArenaChip) > 0,
            "NPC arena fights actually spend attempts, persist records and pay win rewards");
        Console.WriteLine("PASS: separate-process restart resumes the remaining phases exactly once");
        return 0;
    }

    File.WriteAllText(path, JsonConvert.SerializeObject(new CoreInfo
    {
        Users = [new User { ID = 1, Nickname = "UnchangedHuman", LastReset = now, LastWeeklyReset = now }], BondRewardLevelsMigrated = true
    }));
    instance.SetValue(null, JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(path))!);
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted(); db.Database.EnsureCreated();
    db.Users.Add(new GameUser { ID = 1, Nickname = "UnchangedHuman" }); db.SaveChanges();
    ManagedNpcService.CreateBatch(db, 2, "routine-fixture", now);
    foreach (var player in State().Users.Where(u => u.ManagedNpc != null))
    {
        foreach (var stage in data.StageDataRecords.Values.Where(s => s.ChapterMod == ChapterMod.Normal && s.StageType == StageType.Main && s.ChapterId <= 8))
        {
            var map = (string)typeof(GameData).GetMethod("GetMapIdFromChapter", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(data, [stage.ChapterId, stage.ChapterMod])!;
            if (!player.FieldInfoNew.TryGetValue(map, out var field)) player.FieldInfoNew[map] = field = new();
            if (!field.CompletedStages.Contains(stage.Id)) field.CompletedStages.Add(stage.Id);
        }
        player.LastNormalStageCleared = data.StageDataRecords.Values.Where(s => s.ChapterMod == ChapterMod.Normal && s.StageType == StageType.Main && s.ChapterId == 8).Max(s => s.Id);
    }
    var opponent = State().Users.Single(u => u.ID == 1500000001);
    opponent.SynchroDeviceUpgraded = false;
    foreach (var character in opponent.Characters) character.Level = 1;
    int legacyDay = int.Parse(now.AddHours(-State().ResetHourUtcTime).ToString("yyyyMMdd"));
    Npc().ManagedNpc!.Day = legacyDay; Npc().ManagedNpc!.ActivityToday = 30;
    var legacyGuild = State().Guilds[Npc().Guild.guildId!.Value];
    legacyGuild.ActivityByDay = null; legacyGuild.NpcActivityDay = legacyDay;
    legacyGuild.Data.ActivityPoint = legacyGuild.Data.ActivityPointDaily = legacyGuild.Data.Exp = 30;
    Npc().BattleTime = now.AddHours(-24);
    Npc().Currency[CurrencyType.Gold] = 10_000_000;
    Npc().Currency[CurrencyType.CharacterExp] = 10_000_000;
    Npc().Currency[CurrencyType.CharacterExp2] = 1_000_000;
    Npc().Currency[CurrencyType.CharPremiumTicket] = 1;
    var startingLevel = Npc().SynchroDeviceUpgraded ? Npc().SynchroDeviceLevel : Npc().Characters.Max(c => c.Level);
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "reward-tables-before.json"), JsonConvert.SerializeObject(data.RewardDataRecords));
    var uid = Npc().ID;
    var originalCharacters = Npc().Characters.Select(c => c.Csn).ToHashSet();
    var originalStage = Npc().LastNormalStageCleared;
    var originalGuild = Npc().Guild.guildId;
    var human = JsonConvert.SerializeObject(State().Users.Single(u => u.ID == 1));

    ManagedNpcService.Tick(now);
    Check(Completed() == 0, "initial migration schedules without free immediate settlement");
    Check(Npc().GuildContributionByDay.GetValueOrDefault(legacyDay) == 30, "migration retains already-earned legacy guild contribution");
    Check(Npc().ID == uid && Npc().LastNormalStageCleared == originalStage && Npc().Guild.guildId == originalGuild && originalCharacters.SetEquals(Npc().Characters.Select(c => c.Csn)),
        "migration preserves NPC identity, characters, campaign and guild");
    ManagedNpcService.Tick(now.AddMinutes(5));
    Check(Completed() == 0, "phase cannot run before six minutes");
    db.Database.ExecuteSqlRaw("CREATE TRIGGER npc_test_reject BEFORE INSERT ON Triggers WHEN NEW.UserId = 1500000000 BEGIN SELECT RAISE(FAIL, 'NPC notification failure'); END;");
    ManagedNpcService.Tick(now.AddMinutes(6));
    Check(Completed() == 1, "exactly one phase is due at six minutes");
    Check(Npc().ResetableData.WipeoutCount == 1 && Npc().GetCurrencyVal(CurrencyType.CharacterExp) > 10_000_000,
        "first phase settles actual outpost resources and spends the daily free wipeout");
    Check(Npc().PendingMissionTriggers.Count > 0 && !db.Triggers.Any(t => t.UserId == uid), "SQL failure leaves the paid phase and its durable outbox committed");
    var deferred = Snapshot();
    ManagedNpcService.Tick(now.AddMinutes(6));
    Check(Snapshot() == deferred, "notification retry cannot rerun a committed phase");
    db.Database.ExecuteSqlRaw("DROP TRIGGER npc_test_reject;");
    var resourcesBeforeTraining = Npc().GetCurrencyVal(CurrencyType.Gold);
    ManagedNpcService.Tick(now.AddMinutes(12));
    Check(Completed() == 2, "exactly two phases are due at twelve minutes");
    Check(Npc().PendingMissionTriggers.Count == 0 && db.Triggers.Count(t => t.UserId == uid && t.Type == Trigger.OutpostBattleReward) == 1,
        "SQL recovery delivers each earlier outpost action once");
    Check(Npc().BondInfo.Sum(b => b.CounseledCount) > 0 && Npc().ResetableData.DailyCounselCount.GetValueOrDefault(1) == 0,
        "counseling increases bond and consumes its actual daily budget");
    Check(Npc().GetCurrencyVal(CurrencyType.Gold) < resourcesBeforeTraining &&
        (Npc().SynchroDeviceUpgraded ? Npc().SynchroDeviceLevel : Npc().Characters.Max(c => c.Level)) > startingLevel,
        "character training pays actual gold/material costs for its level increase");
    var probe = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(Npc()))!;
    foreach (var character in probe.Characters) character.Skill1Lvl = character.Skill2Lvl = character.UltimateLevel = 1;
    ManagedNpcRoster.Refresh(probe);
    Check(probe.Characters.All(c => c.Skill1Lvl == 1 && c.Skill2Lvl == 1 && c.UltimateLevel == 1), "ongoing team selection cannot grant free skill levels");
    probe.SynchroSlots.Clear(); probe.SynchroSlotsFromItems = 0;
    ManagedNpcGrowth.Refresh(probe);
    Check(probe.SynchroSlots.Count <= 5 && probe.SynchroSlotsFromItems == 0, "ongoing roster growth cannot manufacture paid synchro slots");
    Check(JsonConvert.SerializeObject(State().Users.Single(u => u.ID == 1)) == human, "NPC activities preserve the human profile");
    Console.WriteLine("PASS: migration preserves existing NPCs; phases obey the six-minute schedule");
    Save();
    using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--resume") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory })!)
    {
        await child.WaitForExitAsync();
        Check(child.ExitCode == 0, "independent restart regression");
    }
    instance.SetValue(null, JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(path))!);
    Check(Completed() == 5, "all five phases completed within thirty minutes");
    var done = Snapshot();
    ManagedNpcService.Tick(now.AddHours(3));
    ManagedNpcService.Tick(now.AddMinutes(20));
    Check(Snapshot() == done, "completed day and backward clock cannot execute again");
    Console.WriteLine("PASS: one complete routine per game day; repeated ticks and clock rollback do not repeat rewards");

    ManagedNpcService.Tick(now.AddDays(1));
    Check(Completed() == 0, "new day schedules one new routine without historical catchup");
    ManagedNpcService.Tick(now.AddDays(1).AddMinutes(6));
    Check(Completed() == 1, "new day's first phase");
    ManagedNpcService.Configure(false, 1, now.AddDays(1).AddMinutes(7));
    ManagedNpcService.Tick(now.AddDays(1).AddHours(4));
    Check(Completed() == 1, "pause does not execute remaining phases");
    ManagedNpcService.Configure(true, 1, now.AddDays(1).AddHours(4));
    ManagedNpcService.Tick(now.AddDays(1).AddHours(4));
    Check(Completed() == 1, "resume retains completed phases and reschedules remaining actions");
    var beforeFailure = Snapshot();
    Directory.CreateDirectory(path + ".tmp");
    bool failed = false;
    try { ManagedNpcService.Tick(now.AddDays(1).AddHours(4).AddMinutes(6)); }
    catch (IOException) { failed = true; }
    catch (UnauthorizedAccessException) { failed = true; }
    finally { Directory.Delete(path + ".tmp"); }
    Check(failed && Snapshot() == beforeFailure, "failed phase save rolls back settlement and phase marker");
    ManagedNpcService.Tick(now.AddDays(1).AddHours(4).AddMinutes(6));
    Check(Completed() == 2, "failed phase can retry once without losing progress");
    ManagedNpcService.Tick(now.AddDays(1).AddHours(4).AddMinutes(18));
    Check(Completed() == 4, "prepare an already-settled fourth phase for final-claim rollback");
    beforeFailure = Snapshot();
    int beforeSql = db.Triggers.Count();
    Directory.CreateDirectory(path + ".tmp"); failed = false;
    try { ManagedNpcService.Tick(now.AddDays(1).AddHours(4).AddMinutes(24)); }
    catch (IOException) { failed = true; }
    catch (UnauthorizedAccessException) { failed = true; }
    finally { Directory.Delete(path + ".tmp"); }
    Check(failed && Snapshot() == beforeFailure && db.Triggers.Count() == beforeSql,
        "failed final phase rolls back task rewards, guild contribution, arena world and opponent records before SQLite notification");
    ManagedNpcService.Tick(now.AddDays(1).AddHours(4).AddMinutes(24));
    Check(Completed() == 5, "final phase retries successfully after rollback");
    Console.WriteLine("PASS: cross-day scheduling, pause/resume and failed-save rollback preserve phase accounting");
    return 0;
}
catch (Exception ex) { Console.WriteLine(ex); return 1; }

static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
