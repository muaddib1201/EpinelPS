using System.Reflection;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Services;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

try
{
    if (args.Length != 8 || args[0] != "--workspace" || args[2] != "--account" || args[4] != "--floor" || args[6] != "--report")
        throw new ArgumentException("Expected --workspace <private workspace> --account <ID> --floor <floor> --report <JSON>.");
    string workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1])) + Path.DirectorySeparatorChar;
    string directory = Path.GetFullPath(AppContext.BaseDirectory);
    string report = Path.GetFullPath(args[7]);
    if (!workspace.Contains(Path.DirectorySeparatorChar + ".worktrees" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        !directory.StartsWith(workspace, StringComparison.OrdinalIgnoreCase) || !report.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Repair executable, data and report must remain inside the explicit private worktree workspace.");
    foreach (string name in new[] { "db.json", "epinelps.db" })
        if (!File.Exists(Path.Combine(directory, name))) throw new FileNotFoundException("Stopped-save copy required: " + name);
    var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
    var data = new GameData(pack);
    typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
    await GameDataInitializer.InitializeGameData(new Progress<double>());
    GameContext.InitializeOptions($"Data Source=\"{Path.Combine(directory, "epinelps.db")}\"", "sqlite");
    ulong id = ulong.Parse(args[3]);
    using (var db = GameContext.CreateNew())
        if (!db.Users.Any(u => u.ID == id && u.Nickname == "ZOLRATH")) throw new InvalidDataException("ZOLRATH profile/account mismatch.");
    var result = RankingTowerRepair.Execute(id, int.Parse(args[5]));
    using (var db = GameContext.CreateNew()) db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)");
    File.WriteAllText(report, JsonConvert.SerializeObject(new { status = "repaired", result }, Formatting.Indented));
    Console.WriteLine("Private staged repair completed: " + report);
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
