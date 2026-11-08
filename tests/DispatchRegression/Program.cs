using System.Diagnostics;
using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.LobbyServer.Outpost.Dispatch;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

const ulong account = 98101, other = 98102;
var jsonPath = Path.Combine(AppContext.BaseDirectory, "db.json");
if (args.Contains("--verify-saved"))
{
    var saved = JObject.Parse(File.ReadAllText(jsonPath));
    var player = saved["Users"]!.Single(u => u.Value<ulong>("ID") == account);
    Check(player.Value<int>("DispatchResetCount") == 2 && player["UserDispatchData"]!["dispatchDatas"]!.Count() == 10 &&
        player["Currency"]!["FreeCash"]!.Value<long>() == 950, "reset did not survive a fresh process");
    Console.WriteLine("PASS reset list, count and balance survive a fresh process");
    return 0;
}
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var dbType = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var dbProperty = dbType.GetProperty("Instance")!;
File.WriteAllText(jsonPath, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true, Users = [Fixture(), Fixture(other)] }));
var connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "dispatch.sqlite")}";
GameContext.InitializeOptions(connection, "sqlite");
using (var database = GameContext.CreateNew())
{
    database.Database.EnsureDeleted();
    database.Database.EnsureCreated();
    database.Users.AddRange(new GameUser { ID = account }, new GameUser { ID = other });
    database.SaveChanges();
}
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
int passed = 0, failed = 0;

await Run("academy rewards set three independent levels without expiring today's tasks", () =>
{
    var user = Reset();
    var today = user.UserDispatchData.Today;
    DispatchHelper.SyncDispatchLevel(user);
    Check(user.DispatchLv == 4 && user.DispatchCollectionLv == 0 && user.DispatchFavoriteLv == 0,
        $"expected levels 4/0/0, got {user.DispatchLv}/{user.DispatchCollectionLv}/{user.DispatchFavoriteLv}");
    Check(user.UserDispatchData.Today == today && user.DispatchResetCount == 1, "level synchronization reset daily history");
    user.CompletedTacticAcademyLessons.AddRange([5004, 11002, 13003]);
    DispatchHelper.SyncDispatchLevel(user);
    Check(user.DispatchLv == 4 && user.DispatchCollectionLv == 3 && user.DispatchFavoriteLv == 1, "specialty rewards used the ordinary lesson count");
    return Task.CompletedTask;
});
await Run("legacy 18-task board normalizes to ten unique ordinary tasks", async () =>
{
    Reset();
    var response = await Get();
    Board(response.DispatchList, 10, 0, 0);
    Check(Current().ResetableData.DispatchCount == 10 && response.DispatchResetCount == 1, "normalization changed the reset count or left an inflated allowance");
    var again = await Get();
    Check(response.Equals(again), "a repeated read rerolled the board");
});
await Run("unlocked specialty contributes three slots exactly once", async () =>
{
    var user = Reset();
    user.CompletedTacticAcademyLessons.Add(5004);
    Board((await Get()).DispatchList, 10, 3, 0);
    Check(Current().ResetableData.DispatchCount == 13, "specialty capacity was counted twice");
});
await Run("ten running legacy tasks keep their exact timestamps while surplus pending tasks disappear", async () =>
{
    var user = Reset();
    foreach (var row in user.UserDispatchData.dispatchDatas.Take(10))
    {
        row.IsRun = 1;
        user.SelectableDispatchData.Add(new() { SelectTid = row.Tid, IsRun = true, StartAt = row.StartAt, EndAt = row.EndAt });
    }
    var running = user.UserDispatchData.dispatchDatas.Take(10).Select(r => r.Clone()).ToArray();
    var response = await Get();
    Board(response.DispatchList, 10, 0, 0);
    Check(running.All(r => response.DispatchList.Any(r.Equals)), "normalization changed a running task");
    Check(user.SelectableDispatchData.Count == 10, "normalization lost running selections");
    var before = Snapshot();
    await Reroll();
    Check(JToken.DeepEquals(before, Snapshot()), "reset with no pending tasks charged or changed the board");
});
await Run("reads and resets cannot recreate already claimed daily slots", async () =>
{
    var user = Reset();
    user.DispatchClearList = [8, 47, 18, 3, 19, 9, 15, 46, 22, 23];
    user.UserDispatchData.dispatchDatas = [];
    Check((await Get()).DispatchList.Count == 0 && (await Reroll()).DispatchList.Count == 0, "claimed slots were regenerated");
    Check(user.DispatchResetCount == 1 && user.Currency[CurrencyType.FreeCash] == 1000, "empty reset consumed gems or an attempt");
});
await Run("daily rollover preserves unfinished tasks while refreshing only available slots", async () =>
{
    var user = Reset();
    var running = user.UserDispatchData.dispatchDatas[0];
    running.IsRun = 1;
    var expected = running.Clone();
    user.UserDispatchData.Today = 20260101;
    user.DispatchClearList.Add(47);
    Board((await Get()).DispatchList, 10, 0, 0);
    Check(user.UserDispatchData.dispatchDatas.Any(expected.Equals) && user.DispatchClearList.Count == 0 && user.DispatchResetCount == 0,
        "rollover lost a running task or retained yesterday's counters");
});
await Run("reset rolls the current board probabilities and preserves running and claimed tasks", async () =>
{
    var user = Reset();
    await Get();
    var running = user.UserDispatchData.dispatchDatas[0];
    running.IsRun = 1;
    var expected = running.Clone();
    var board = data.DispatchBoardTable[23204];
    var probabilities = board.DispatchList;
    board.DispatchList = [new() { DispatchGroup = 40000, DispatchProb = 100 }];
    try
    {
        var response = await Reroll();
        Board(response.DispatchList, 10, 0, 0);
        Check(response.DispatchList.Any(expected.Equals), "reset altered an in-flight task");
        Check(response.DispatchList.Where(r => r.IsRun == 0).All(r => data.DispatchTable[r.Tid].DispatchGroup == 40000),
            "reset reused old groups instead of the current board probabilities");
        Check(user.Currency[CurrencyType.FreeCash] == 950 && response.Currencies.Single(c => c.Type == 99).Value == 950 && response.DispatchResetCount == 2,
            "reset did not debit 50 gems or returned the price as the balance");
    }
    finally { board.DispatchList = probabilities; }
});
await Run("paid gem fallback accepts an exact balance and returns both actual balances", async () =>
{
    var user = Reset();
    await Get();
    user.Currency[CurrencyType.FreeCash] = 20;
    user.Currency[CurrencyType.ChargeCash] = 30;
    var response = await Reroll();
    Check(user.Currency[CurrencyType.FreeCash] == 0 && user.Currency[CurrencyType.ChargeCash] == 0 &&
        response.Currencies.Single(c => c.Type == 99).Value == 0 && response.Currencies.Single(c => c.Type == 98).Value == 0,
        "exact paid/free split did not settle atomically");
});
await Run("insufficient gems leave tasks, count and both accounts unchanged", async () =>
{
    var user = Reset();
    await Get();
    user.Currency[CurrencyType.FreeCash] = 49;
    var before = Snapshot();
    await Reject(Reroll);
    Check(JToken.DeepEquals(before, Snapshot()), "rejected reset partially changed state");
});
await Run("a failed save rolls back reset tasks, count and payment", async () =>
{
    Reset();
    await Get();
    var before = Snapshot();
    Directory.CreateDirectory(jsonPath + ".tmp");
    try
    {
        bool rejected = false;
        try { await Reroll(); }
        catch (IOException) { rejected = true; }
        catch (UnauthorizedAccessException) { rejected = true; }
        Check(rejected && JToken.DeepEquals(before, Snapshot()), "failed save reported success or left a partial reset");
    }
    finally { Directory.Delete(jsonPath + ".tmp"); }
});
await Run("exhausted configured pools reject without partially replacing or charging", async () =>
{
    Reset();
    await Get();
    var board = data.DispatchBoardTable[23204];
    var probabilities = board.DispatchList;
    board.DispatchList = [new() { DispatchGroup = -98101, DispatchProb = 100 }];
    var before = Snapshot();
    try
    {
        await Reject(Reroll);
        Check(JToken.DeepEquals(before, Snapshot()), "exhausted pool changed the player");
    }
    finally { board.DispatchList = probabilities; }
});
await Run("dispatch start, cancel and read wait for the same player lock as reset", async () =>
{
    var user = Reset();
    await Get();
    int tid = user.UserDispatchData.dispatchDatas[0].Tid;
    foreach (var request in new (string Route, IMessage Body)[] {
        ("/outpost/rundispatch", new ReqRunDispatch { Tid = tid }),
        ("/outpost/canceldispatch", new ReqCancelDispatch { Tid = tid }),
        ("/outpost/rundispatchlist", new ReqRunDispatchList { DispatchData = { new NetDispatchData { Tid = tid } } }),
        ("/outpost/dispatch/get", new ReqGetDispatchList()) })
    {
        using var started = new ManualResetEventSlim();
        Task requestTask;
        bool ranWhileLocked;
        lock (user)
        {
            requestTask = Task.Run(async () => { started.Set(); await Send<ResGetDispatchList>(request.Route, request.Body); });
            Check(started.Wait(TimeSpan.FromSeconds(5)), "request did not start");
            ranWhileLocked = requestTask.Wait(TimeSpan.FromMilliseconds(200));
        }
        await requestTask;
        Check(!ranWhileLocked, request.Route + " bypassed the player lock");
    }
});
await Run("a completed preserved task remains claimable exactly once", async () =>
{
    var user = Reset();
    var running = user.UserDispatchData.dispatchDatas[0];
    running.IsRun = 1;
    running.EndAt = DateTime.UtcNow.AddMinutes(-1).Ticks;
    await Get();
    var request = new ReqObtainDispatchReward { TidList = { running.Tid } };
    await Send<ResObtainDispatchReward>("/outpost/obtaindispatchreward", request);
    var afterClaim = JObject.FromObject(new { user.Currency, user.Items });
    await Send<ResObtainDispatchReward>("/outpost/obtaindispatchreward", request);
    Check(user.DispatchClearList.Contains(running.Tid) && !user.UserDispatchData.dispatchDatas.Any(r => r.Tid == running.Tid) &&
        JToken.DeepEquals(afterClaim, JObject.FromObject(new { user.Currency, user.Items })), "claim was lost or repeated rewards");
    Check((await Get()).DispatchList.Count == 9, "read recreated the claimed slot");
});
await Run("reset commits tasks and balances before any subsequent request or restart", async () =>
{
    Reset();
    await Get();
    var response = await Reroll();
    var saved = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(jsonPath))!.Users.Single(u => u.ID == account);
    Check(saved.UserDispatchData.dispatchDatas.SequenceEqual(response.DispatchList) && saved.DispatchResetCount == 2 && saved.Currency[CurrencyType.FreeCash] == 950,
        "reset was not persisted by its own request");
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--verify-saved") { UseShellExecute = false, CreateNoWindow = true })!;
    await child.WaitForExitAsync();
    Check(child.ExitCode == 0, "fresh-process verification failed");
});
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

User Fixture(ulong id = account) => new()
{
    ID = id, InfraCoreLvl = 20, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow,
    CompletedTacticAcademyLessons = [2002, 3004, 4003], DispatchLv = 3, DispatchCollectionLv = 3, DispatchFavoriteLv = 3, DispatchResetCount = 1,
    Currency = new() { [CurrencyType.FreeCash] = 1000, [CurrencyType.ChargeCash] = 0 },
    UserDispatchData = new() { Today = int.Parse(DateTime.UtcNow.AddHours(4).ToString("yyyyMMdd")), dispatchDatas =
        new[] { 8, 47, 18, 3, 19, 9, 15, 46, 22, 23, 55, 2, 11, 14, 30031, 30049, 30036, 30047 }
        .Select(tid => new NetUserDispatchData { Tid = tid, StartAt = DateTime.UtcNow.Ticks, EndAt = DateTime.UtcNow.AddHours(1).Ticks }).ToList() },
    ResetableData = new() { DispatchCount = 14 }
};
User Current() => ((CoreInfo)dbProperty.GetValue(null)!).Users.Single(u => u.ID == account);
User Reset()
{
    var first = Fixture();
    dbProperty.SetValue(null, new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true, Users = [first, Fixture(other)] });
    dbType.GetMethod("Save")!.Invoke(null, null);
    return first;
}
JToken Snapshot() => JToken.FromObject(dbProperty.GetValue(null)!);
Task<ResGetDispatchList> Get() => Send<ResGetDispatchList>("/outpost/dispatch/get", new ReqGetDispatchList());
Task<ResResetDispatchList> Reroll() => Send<ResResetDispatchList>("/Outpost/ResetDispatchList", new ReqResetDispatchList());
void Board(IEnumerable<NetUserDispatchData> rows, int ordinary, int collection, int favorite)
{
    var list = rows.ToList();
    Check(list.Count == ordinary + collection + favorite && list.Select(r => r.Tid).Distinct().Count() == list.Count, "incorrect or duplicate total: " + list.Count);
    Check(list.Count(r => data.DispatchTable[r.Tid].DispatchType == DispatchType.Dispatch) == ordinary &&
        list.Count(r => data.DispatchTable[r.Tid].DispatchType == DispatchType.DispatchCollection) == collection &&
        list.Count(r => data.DispatchTable[r.Tid].DispatchType == DispatchType.DispatchFavorite) == favorite, "dispatch category capacity is incorrect");
}
async Task<T> Send<T>(string route, IMessage request) where T : IMessage<T>, new()
{
    using var scope = services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Items["UserID"] = account;
    ctx.Request.Path = "/v1" + route;
    ctx.Request.Body = new MemoryStream(request.ToByteArray());
    ctx.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    var response = new T();
    response.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray());
    return response;
}
async Task Reject(Func<Task<ResResetDispatchList>> request)
{
    try { await request(); }
    catch (BadHttpRequestException) { return; }
    throw new InvalidOperationException("expected a rejected reset");
}
async Task Run(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
