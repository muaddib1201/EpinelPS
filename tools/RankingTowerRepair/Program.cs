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
    if (File.Exists(report) || Directory.Exists(report))
        throw new InvalidOperationException("Repair report must be a new file; input/output path collision rejected.");
    NoReparsePoints(directory);
    NoReparsePoints(Path.GetDirectoryName(report)!);
    foreach (string name in new[] { "db.json", "epinelps.db" })
    {
        string input = Path.Combine(directory, name);
        if (!File.Exists(input)) throw new FileNotFoundException("Stopped-save copy required: " + name);
        NoReparsePoints(input);
    }
    foreach (string sidecar in new[] { "epinelps.db-wal", "epinelps.db-shm" })
        if (File.Exists(Path.Combine(directory, sidecar))) NoReparsePoints(Path.Combine(directory, sidecar));
    var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
    NoReparsePoints(pack);
    // Reserve a new report before any initialization can write data; keep the
    // handle exclusive so report output cannot replace an input or executable.
    using var reportStream = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    var data = new GameData(pack);
    typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
    await GameDataInitializer.InitializeGameData(new Progress<double>());
    GameContext.InitializeOptions($"Data Source=\"{Path.Combine(directory, "epinelps.db")}\"", "sqlite");
    ulong id = ulong.Parse(args[3]);
    using (var db = GameContext.CreateNew())
        if (!db.Users.Any(u => u.ID == id && u.Nickname == "ZOLRATH")) throw new InvalidDataException("ZOLRATH profile/account mismatch.");
    var result = RankingTowerRepair.Execute(id, int.Parse(args[5]));
    using (var db = GameContext.CreateNew()) db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)");
    using var writer = new StreamWriter(reportStream);
    await writer.WriteAsync(JsonConvert.SerializeObject(new { status = "repaired", result,
        currencyTypes = Enum.GetValues<CurrencyType>().ToDictionary(value => (int)value, value => value.ToString()),
        towerTypes = Enum.GetValues<CorporationTowerType>().ToDictionary(value => (int)value, value => value.ToString()),
        rewardKinds = new { currency = (int)RewardType.Currency, item = (int)RewardType.Item },
        userExpTable = data.UserExpDataRecords.Values.Select(t => new { level = t.Level, exp = t.Exp }),
        towerFloors = data.towerTable.Values.Select(t => new { id = t.Id, type = (int)t.Type, floor = t.Floor }),
        targets = data.Rankings.Values.Select(t => new { id = t.Id, type = (int)t.RankingType, conditionId = t.ConditionId, conditionValue = t.ConditionValue })
    }, Formatting.Indented));
    await writer.FlushAsync();
    Console.WriteLine("Private staged repair completed: " + report);
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static void NoReparsePoints(string path)
{
    FileSystemInfo? current = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
    while (current != null)
    {
        if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Private repair paths must not contain junctions or symbolic links.");
        current = current is DirectoryInfo folder ? folder.Parent : ((FileInfo)current).Directory;
    }
}
