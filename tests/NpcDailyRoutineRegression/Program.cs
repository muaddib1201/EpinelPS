using System.Diagnostics;
using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    User Npc() => State().Users.Single(u => u.ManagedNpc != null);
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
    ManagedNpcService.CreateBatch(db, 1, "routine-fixture", now);
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
    Check(Npc().ID == uid && Npc().LastNormalStageCleared == originalStage && Npc().Guild.guildId == originalGuild && originalCharacters.SetEquals(Npc().Characters.Select(c => c.Csn)),
        "migration preserves NPC identity, characters, campaign and guild");
    ManagedNpcService.Tick(now.AddMinutes(5));
    Check(Completed() == 0, "phase cannot run before six minutes");
    ManagedNpcService.Tick(now.AddMinutes(6));
    Check(Completed() == 1, "exactly one phase is due at six minutes");
    Check(Npc().ResetableData.WipeoutCount == 1 && Npc().GetCurrencyVal(CurrencyType.CharacterExp) > 10_000_000,
        "first phase settles actual outpost resources and spends the daily free wipeout");
    var resourcesBeforeTraining = Npc().GetCurrencyVal(CurrencyType.Gold);
    ManagedNpcService.Tick(now.AddMinutes(12));
    Check(Completed() == 2, "exactly two phases are due at twelve minutes");
    Check(Npc().BondInfo.Sum(b => b.CounseledCount) > 0 && Npc().ResetableData.DailyCounselCount.GetValueOrDefault(1) == 0,
        "counseling increases bond and consumes its actual daily budget");
    Check(Npc().GetCurrencyVal(CurrencyType.Gold) < resourcesBeforeTraining &&
        (Npc().SynchroDeviceUpgraded ? Npc().SynchroDeviceLevel : Npc().Characters.Max(c => c.Level)) > startingLevel,
        "character training pays actual gold/material costs for its level increase");
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
    Console.WriteLine("PASS: cross-day scheduling, pause/resume and failed-save rollback preserve phase accounting");
    return 0;
}
catch (Exception ex) { Console.WriteLine(ex); return 1; }

static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
