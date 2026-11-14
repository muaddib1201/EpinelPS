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
var now = DateTime.UtcNow;
var season = new SoloRaidSeasonConfig { RaidId = 1000044, VisibleAt = now.AddDays(-5), StartAt = now.AddDays(-5), EndAt = now.AddDays(2), SettleAt = now.AddDays(2), DisableAt = now.AddDays(9) };
typeof(GameConfig).GetField("_root", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, new GameConfigRoot { SoloRaidAutoSync = false, SoloRaidSeason = season });
SoloRaidSeasonSource.Bind(null);
var manager = data.SoloRaidManagerTable[season.RaidId];
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "npc-solo-raid-definitions.json"), JsonConvert.SerializeObject(new
{
    Config = data.GameSettings.Where(p => p.Key.Contains("raid", StringComparison.OrdinalIgnoreCase)),
    Presets = data.SoloRaidPresetTable.Values.Where(p => p.PresetGroupId == manager.MonsterPreset),
    Unlock = data.ContentsOpenTable.GetValueOrDefault(ContentsOpen.SoloRaid)
}, Formatting.Indented));
var json = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var instance = json.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true }));
instance.SetValue(null, new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true });
CoreInfo State() => (CoreInfo)instance.GetValue(null)!;
GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "npc-solo-raid.db")}", "sqlite");
using var db = GameContext.CreateNew();
db.Database.EnsureDeleted(); db.Database.EnsureCreated();
ManagedNpcService.CreateBatch(db, 1, "solo-raid-fixture", now);
var npc = State().Users.Single();
npc.ManagedNpc!.DailyRoutine = new NpcDailyRoutineState { Day = int.Parse(now.AddHours(-State().ResetHourUtcTime).ToString("yyyyMMdd")), CompletedPhases = 5, StartedUtc = now.AddMinutes(-30), LastCompletedUtc = now.AddMinutes(-5) };
npc.ManagedNpc.LastActionUtc = now.AddMinutes(-5);
var raid = new SoloRaidInfo { RaidId = season.RaidId, LastDateDay = npc.GetDateDay() };
for (int level = 1; level <= 7; level++) raid.SoloRaidLevels.Add(new SoloRaidLevelData { RaidLevel = level, Type = SoloRaidType.Normal, IsClear = true });
npc.SoloRaidData[season.RaidId] = raid;
ManagedNpcService.Tick(now);
if (!raid.SoloRaidLevels.Any(l => l.Type == SoloRaidType.Trial && l.IsClear && !l.IsOpen && l.TotalDamage > 0))
{
    Console.WriteLine("FAIL completed daily routine must also settle an owned-squad Solo Raid challenge");
    return 1;
}
Console.WriteLine("PASS completed daily routine also settles an owned-squad Solo Raid challenge");
return 0;
