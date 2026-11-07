using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;

try
{
if (args.Contains("--check-startup-config"))
{
    var config = new ConfigurationBuilder().AddCommandLine(args.Skip(1).ToArray()).Build();
    if (!config.GetValue<bool>("seed-managed-npcs")) { Console.WriteLine("FAIL: seed switch was consumed by the headless switch"); return 1; }
    Console.WriteLine("PASS: startup arguments enable initial NPC creation");
    return 0;
}
var assembly = typeof(GameContext).Assembly;
var service = assembly.GetType("EpinelPS.Services.ManagedNpcService");
if (service == null) { Console.WriteLine("FAIL: managed NPC creation is unavailable"); return 1; }
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
if (root == null) throw new Exception("Cannot locate isolated regression fixture data");
if (args.Contains("--probe-live-readonly")) return await LiveChecks.Run(root.FullName);
if (args.FirstOrDefault(a => a.StartsWith("--assign-live-guild=")) is string guildAssignment)
    return await GuildAssignment.Run(root.FullName, guildAssignment["--assign-live-guild=".Length..]);
var pack = Directory.GetFiles(Path.Combine(root.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
if (args.Contains("--check-breakthroughs")) return BreakthroughChecks.Run(data);
if (args.Contains("--check-growth")) return GrowthChecks.Run(data);
if (args.Contains("--check-teams")) return TeamChecks.Run(root.FullName, data);
if (args.Contains("--check-social")) return SocialChecks.Run(root.FullName);
if (args.Contains("--verify-team-saved")) return TeamChecks.VerifySaved(data);
if (args.Contains("--audit-live-rosters")) return TeamChecks.AuditLive(root.FullName, data);
var path = Path.Combine(AppContext.BaseDirectory, "db.json");
if (args.Contains("--verify-saved"))
{
    var restartDb = assembly.GetType("EpinelPS.Database.JsonDb")!;
    var restartCore = (CoreInfo)restartDb.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "npc-regression.db")}", "sqlite");
    using var context = GameContext.CreateNew();
    var restartNpcs = restartCore.Users.Where(u => u.ManagedNpc != null).ToArray();
    Check(restartNpcs.Length == 32 && restartCore.NpcManagement!.PendingUsers.Count == 0, "restart must retain completed creation batches");
    Check(restartNpcs.All(u => context.Users.Any(p => p.ID == u.ID) && !context.SdkUsers.Any(s => s.ID == u.ID)), "restart profiles must exist without login accounts");
    long beforeActions = restartNpcs.Sum(u => u.ManagedNpc!.TotalActions);
    Check(EpinelPS.Services.ManagedNpcService.EnsureInitial(context, DateTime.UtcNow) == 0, "initial seed must be idempotent across process restarts");
    EpinelPS.Services.ManagedNpcService.Tick(DateTime.UtcNow);
    Check(restartCore.Users.Where(u => u.ManagedNpc != null).Sum(u => u.ManagedNpc!.TotalActions) == beforeActions, "restart with an earlier clock must preserve progress");
    Console.WriteLine("PASS: separate-process restart preserves 32 NPCs, profiles, batches and growth state");
    Console.WriteLine("PASS: repeated initial seed and backward-clock startup cannot duplicate NPCs or progress");
    return 0;
}
var human = new User { ID = 1, LastNormalStageCleared = 6000002 };
File.WriteAllText(path, JsonConvert.SerializeObject(new CoreInfo { Users = [human], BondRewardLevelsMigrated = true }));
var jsonDb = assembly.GetType("EpinelPS.Database.JsonDb")!;
var instance = jsonDb.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "npc-regression.db")}", "sqlite");
using var db = GameContext.CreateNew();
db.Database.EnsureDeleted(); db.Database.EnsureCreated();
db.Users.Add(new GameUser { ID = 1, Nickname = "Human" }); db.SaveChanges();
var now = new DateTime(2026, 11, 6, 4, 0, 0, DateTimeKind.Utc);
Call("Create", db, 30, now);
var saved = (CoreInfo)instance.GetValue(null)!;
Check(saved.Users.Count == 31, "creation must add exactly 30 NPCs and preserve the human");
Check(saved.Guilds.Count == 5, "initial batch must create five unions");
Check(saved.Users.Count(u => u.Guild.guildId != null) == 30, "initial population is distributed across five unions");
Check(db.SdkUsers.Count() == 0, "NPCs must not receive login accounts");
Check(db.Users.Count() == 31 && db.Users.Find(1UL)!.Nickname == "Human", "SQL profiles must preserve humans");
var npcs = saved.Users.Where(u => u.ID != 1).ToArray();
Check(npcs.Select(u => u.LastNormalStageCleared).Distinct().Count() > 10, "NPC progress should vary");
Check(npcs.Select(u => u.userPointData.UserLevel).Distinct().Count() > 10, "NPC levels should vary");
foreach (var user in npcs)
{
    Check(user.RepresentationTeamDataNew.Length == 5 && user.RepresentationTeamDataNew.Distinct().Count() == 5, "owned five-character squad");
    Check(user.RepresentationTeamDataNew.All(c => user.Characters.Any(x => x.Csn == c)), "squad serial numbers must resolve");
    Check(user.Characters.All(c => data.CharacterTable.ContainsKey(c.Tid)), "owned characters must exist in the data table");
    Check(user.RepresentationTeamDataNew.Sum(c => EpinelPS.Utils.FormulaUtils.CalculateCP(user, c)) > 0,
        "leveled NPC squads must have positive combat power");
    Check(data.StageDataRecords.ContainsKey(user.LastNormalStageCleared), "valid campaign stage");
    Check(user.FieldInfoNew.Values.Any(f => f.CompletedStages.Contains(user.LastNormalStageCleared)), "campaign fields must reflect the last cleared stage");
}
var before = JsonConvert.SerializeObject(saved);
Call("Recover", db);
Check(JsonConvert.SerializeObject(instance.GetValue(null)) == before && db.Users.Count() == 31, "recovery must be idempotent");
Console.WriteLine("PASS: independent creation, profiles, unions, valid squads, progress and idempotent recovery");
if (service.GetMethod("Tick") == null) { Console.WriteLine("FAIL: persistent NPC growth is unavailable"); return 1; }
foreach (var user in npcs)
{
    user.ManagedNpc!.ActiveHours = 24;
    user.ManagedNpc.NextActionUtc = now.AddMinutes(1);
}
for (int hour = 1; hour <= 24; hour++) Call("Tick", now.AddHours(hour));
saved = (CoreInfo)instance.GetValue(null)!;
npcs = saved.Users.Where(u => u.ManagedNpc != null).ToArray();
Check(npcs.All(u => u.ManagedNpc!.TotalActions > 0), "all enabled NPCs should act");
Check(npcs.All(u => u.ManagedNpc!.StagesToday <= 4 + u.ManagedNpc.Tier * 4), "per-day campaign limits");
Check(npcs.All(u => u.ManagedNpc!.Actions.Count <= 50), "bounded action logs");
if (!npcs.Any(u => u.ManagedNpc!.TotalStages > 0))
    foreach (var u in npcs.Take(3))
        Console.WriteLine($"DIAG: {u.ManagedNpc!.Name} level={u.Characters[0].Level} CP={u.RepresentationTeamDataNew.Sum(c => EpinelPS.Utils.FormulaUtils.CalculateCP(u,c))} stage={u.LastNormalStageCleared} power={data.StageDataRecords[u.LastNormalStageCleared].StandardBattlePower} statGroup={data.CharacterTable[u.Characters[0].Tid].StatEnhanceId} statRows={data.characterStatTable.Count}");
Check(npcs.Any(u => u.ManagedNpc!.TotalStages > 0), "some NPCs should advance campaign");
var repeated = JsonConvert.SerializeObject(saved);
Call("Tick", now.AddHours(24));
Check(JsonConvert.SerializeObject(instance.GetValue(null)) == repeated, "same timestamp must not award progress twice");
Call("Tick", now.AddHours(23));
Check(JsonConvert.SerializeObject(instance.GetValue(null)) == repeated, "clock rollback must not award or reverse progress");
Call("Configure", false, 1d, now.AddHours(24));
Call("Tick", now.AddDays(10));
var paused = (CoreInfo)instance.GetValue(null)!;
var totalPaused = paused.Users.Where(u => u.ManagedNpc != null).Sum(u => u.ManagedNpc!.TotalActions);
Call("Configure", true, 1d, now.AddDays(10));
Call("Tick", now.AddDays(10));
Check(((CoreInfo)instance.GetValue(null)!).Users.Where(u => u.ManagedNpc != null).Sum(u => u.ManagedNpc!.TotalActions) == totalPaused,
    "resume must not simulate paused days");
var snapshot = JsonConvert.SerializeObject(instance.GetValue(null));
Directory.CreateDirectory(path + ".tmp");
bool failed = false;
try { Call("Tick", now.AddDays(11)); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
finally { Directory.Delete(path + ".tmp"); }
Check(failed && JsonConvert.SerializeObject(instance.GetValue(null)) == snapshot, "save failure must roll back NPC and union changes");
Console.WriteLine("PASS: varied persistent growth, daily caps, replay, backward clock, pause, bounded logs and failed-save rollback");
db.ChangeTracker.Clear();
db.Database.EnsureDeleted();
DbInitializer.Initialize(db);
Check(!db.SdkUsers.Any(u => u.ID != 1), "startup migration must not turn NPCs into login accounts");
Check(db.Users.Count() == 31, "startup migration must preserve all NPC profile rows");
await HttpChecks.Run((CoreInfo)instance.GetValue(null)!, data, db, $"Data Source={Path.Combine(AppContext.BaseDirectory, "npc-regression.db")}");
await DeepChecks.Run(() => (CoreInfo)instance.GetValue(null)!, value => instance.SetValue(null, value), db);
return 0;

object? Call(string name, params object[] args)
{
    try { return service!.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args); }
    catch (TargetInvocationException ex) { throw ex.InnerException!; }
}
void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
}
catch (Exception ex) { Console.WriteLine(ex); return 1; }
