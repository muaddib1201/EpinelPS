using System.Diagnostics;
using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Reproduces the 2026-11-18 00:10 stall: about one hundred NPCs settled their first daily
// phase against a large save, and player requests waited on JsonDb.SaveLock until the
// client timed out. Players must keep getting the lock while the population settles.
try
{
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
    if (root == null) throw new Exception("Isolated fixture data missing");
    var pack = Directory.GetFiles(Path.Combine(root.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
    var data = new GameData(pack);
    typeof(GameData).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, data);
    await GameDataInitializer.InitializeGameData(new Progress<double>());

    var json = typeof(GameContext).Assembly.GetType("EpinelPS.Database.JsonDb")!;
    var instance = json.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
    var saveLock = json.GetField("SaveLock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var path = Path.Combine(AppContext.BaseDirectory, "db.json");
    GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "npc-background-regression.db")}", "sqlite");
    var now = new DateTime(2026, 11, 8, 5, 0, 0, DateTimeKind.Utc);
    CoreInfo State() => (CoreInfo)instance.GetValue(null)!;
    const int population = 80;

    // A human whose inventory makes the save about as large as the live one (~14 MB).
    var human = new User { ID = 1, Nickname = "WaitingHuman", LastReset = now, LastWeeklyReset = now };
    for (int i = 0; i < 60_000; i++)
        human.Items.Add(new DbItemData { ItemType = 1_000_000 + i, Csn = 10_000_000 + i, Count = i % 97 + 1, Level = i % 15, Exp = i, Position = i % 7, Corp = i % 5 });
    File.WriteAllText(path, JsonConvert.SerializeObject(new CoreInfo { Users = [human], BondRewardLevelsMigrated = true }));
    instance.SetValue(null, JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(path))!);
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted(); db.Database.EnsureCreated();
    db.Users.Add(new GameUser { ID = 1, Nickname = "WaitingHuman" }); db.SaveChanges();
    ManagedNpcService.CreateBatch(db, population, "background-fixture", now);
    var npcs = State().Users.Where(u => u.ManagedNpc != null).Select(u => u.ID).ToArray();
    Check(npcs.Length == population, "fixture creates the NPC population");
    ManagedNpcService.Tick(now); // schedules today's routine; phase one is due five minutes later
    Console.WriteLine($"Fixture save size: {new FileInfo(path).Length / 1024 / 1024} MB, {population} NPCs");
    string humanBefore = JsonConvert.SerializeObject(State().Users.Single(u => u.ID == 1));

    int Phases(User user) => (int)(JObject.FromObject(user.ManagedNpc!)["DailyRoutine"]?["CompletedPhases"] ?? 0);
    var waits = new List<double>();
    var elapsed = Stopwatch.StartNew();
    var tick = Task.Run(() => ManagedNpcService.TickAsync(now.AddMinutes(5)));
    while (!tick.IsCompleted)
    {
        // A player request takes the same lock for its own change.
        var wait = Stopwatch.StartNew();
        lock (saveLock) wait.Stop();
        waits.Add(wait.Elapsed.TotalMilliseconds);
        Thread.Sleep(20);
    }
    await tick;
    elapsed.Stop();
    waits.Sort();
    double worst = waits.Count == 0 ? 0 : waits[^1], p95 = waits.Count == 0 ? 0 : waits[(int)(waits.Count * .95)];
    Console.WriteLine($"Settlement took {elapsed.Elapsed.TotalSeconds:F1} s; player lock probes {waits.Count}, p95 {p95:F0} ms, worst {worst:F0} ms");

    Check(State().Users.Where(u => u.ManagedNpc != null).All(u => Phases(u) == 1), "every NPC settles phase one");
    Check(waits.Count >= 10, "player requests keep getting turns during the settlement");
    Check(worst < 1500, $"a player request waited {worst:F0} ms behind NPC settlement");
    Console.WriteLine("PASS: player requests keep getting the save lock while the whole population settles");

    var stored = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(path))!;
    Check(stored.Users.Where(u => u.ManagedNpc != null).All(u => Phases(u) == 1), "the batch writes every settled phase to disk");
    Check(State().Users.Where(u => u.ManagedNpc != null).All(u => u.PendingMissionTriggers.Count == 0),
        "queued NPC notifications are delivered after the batch is durable");
    Check(JsonConvert.SerializeObject(State().Users.Single(u => u.ID == 1)) == humanBefore, "NPC settlement leaves the human account unchanged");
    Console.WriteLine("PASS: one batched save persists all phases and then delivers NPC notifications");

    string settled = JsonConvert.SerializeObject(State());
    ManagedNpcService.Tick(now.AddMinutes(6));
    Check(JsonConvert.SerializeObject(State()) == settled, "a repeated tick cannot settle phase one again");
    Console.WriteLine("PASS: batched settlement does not replay phases");
    return 0;
}
catch (Exception ex) { Console.WriteLine(ex); return 1; }

static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
