using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.LobbyServer.Tower;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// Use the real static tables and an isolated SQLite database, never the player's saves.
var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
var pack = Path.GetFullPath(Path.Combine(sourceRoot,
    "../../Server and Server selector/cache/prdenv/153-cb882d4b30/staticdata/data/qa-261029-10b/578481/mpk/StaticData.pack"));
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
var progress = new Progress<double>();
foreach (var row in await data.LoadZip<TowerRecord>("TowerTable.json", progress)) data.towerTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<TowerInfoRecord>("TowerInfoTable.json", progress)) data.TowerInfoTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<RewardRecord>("RewardTable.json", progress)) data.RewardDataRecords.Add(row.Id, row);
foreach (var row in await data.LoadZip<UserExpRecord>("UserExpTable.json", progress)) data.UserExpDataRecords.Add(row.Level, row);

string sqlitePath = Path.Combine(AppContext.BaseDirectory, "tower-regression.db");
GameContext.InitializeOptions($"Data Source={sqlitePath}", "sqlite");
using (var db = GameContext.CreateNew())
{
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
}
var jsonDbProperty = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!
    .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!;
int failed = 0;

await Test("floor 2 victory returns its reward when floor 1 is only in SQLite", async () =>
{
    var user = CreateUser(90002, 1);
    RecordClear(user.ID, Trigger.TowerBasicClear, 10001);
    var response = await Send<ResClearTower>(user, new ClearTower(),
        new ReqClearTower { TowerId = 10002, TeamNumber = 1, BattleResult = 1 });
    Check(response.Reward != null && response.Reward.Currency.Count > 0, "victory must return rewards");
    Check(user.TowerProgress[CorporationTowerType.ALL] == 2, "saved progress must advance to floor 2");
    using var db = GameContext.CreateNew();
    Check(db.Triggers.Count(t => t.UserId == user.ID && t.Type == Trigger.TowerBasicClear && t.ConditionId == 10002) == 1,
        "floor 2 must be recorded once in SQLite");
    await Send<ResClearTower>(user, new ClearTower(),
        new ReqClearTower { TowerId = 10003, TeamNumber = 1, BattleResult = 1 });
    Check(user.TowerProgress[CorporationTowerType.ALL] == 3, "the next floor must also work without restarting");
});

await Test("a repeated floor 1 clear cannot pay the reward twice", () =>
{
    var user = CreateUser(90003);
    TowerHelper.CompleteTower(user, 10001);
    var before = user.Currency.ToDictionary();
    ExpectFailure(() => TowerHelper.CompleteTower(user, 10001), "floor already cleared");
    Check(before.OrderBy(x => x.Key).SequenceEqual(user.Currency.OrderBy(x => x.Key)), "duplicate clear must not change currencies");
    return Task.CompletedTask;
});

await Test("another user's clear cannot satisfy the previous-floor requirement", () =>
{
    var user = CreateUser(90004);
    RecordClear(90002, Trigger.TowerBasicClear, 10001);
    ExpectFailure(() => TowerHelper.CompleteTower(user, 10002), "previous floor 1 not cleared");
    Check(!user.TowerProgress.ContainsKey(CorporationTowerType.ALL), "failed clear must not change progress");
    return Task.CompletedTask;
});

await Test("an unrelated trigger cannot satisfy the previous-floor requirement", () =>
{
    var user = CreateUser(90005);
    RecordClear(user.ID, Trigger.TowerAllStart, 10001);
    ExpectFailure(() => TowerHelper.CompleteTower(user, 10002), "previous floor 1 not cleared");
    return Task.CompletedTask;
});

await Test("admin skip rejects a target already recorded in SQLite", () =>
{
    var user = CreateUser(90006, 1);
    RecordClear(user.ID, Trigger.TowerBasicClear, 10003);
    var before = user.Currency.ToDictionary();
    ExpectFailure(() => TowerHelper.SkipTowerFloors(user, 10003, out _), "floor already cleared");
    Check(before.OrderBy(x => x.Key).SequenceEqual(user.Currency.OrderBy(x => x.Key)), "rejected skip must not award currency");
    return Task.CompletedTask;
});

Console.WriteLine($"Tower regression: {5 - failed}/5 passed");
Environment.ExitCode = failed == 0 ? 0 : 1;

User CreateUser(ulong id, int floor = 0)
{
    var user = new User { ID = id, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow };
    user.userPointData.UserLevel = 7;
    if (floor > 0) user.TowerProgress[CorporationTowerType.ALL] = floor;
    jsonDbProperty.SetValue(null, new CoreInfo { Users = [user] });
    using var db = GameContext.CreateNew();
    db.Users.Add(new GameUser { ID = id });
    db.SaveChanges();
    return user;
}

void RecordClear(ulong id, Trigger type, int towerId)
{
    using var db = GameContext.CreateNew();
    db.Triggers.Add(new TriggerModelNew { UserId = id, Type = type, ConditionId = towerId, Value = 1 });
    db.SaveChanges();
}

async Task Test(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine($"PASS: {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL: {name}: {ex.Message}"); }
}

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

void ExpectFailure(Action run, string message)
{
    try { run(); }
    catch (Exception ex) when (ex.Message.Contains(message)) { return; }
    throw new Exception($"expected rejection: {message}");
}

async Task<T> Send<T>(User user, LobbyMessage handler, IMessage request) where T : IMessage<T>, new()
{
    using var services = new ServiceCollection().AddSingleton(GameContext.CreateNew()).BuildServiceProvider();
    var context = new DefaultHttpContext { RequestServices = services };
    context.Items["UserID"] = user.ID;
    context.Request.Body = new MemoryStream(request.ToByteArray());
    var responseBody = new MemoryStream();
    context.Response.Body = responseBody;
    await handler.HandleAsync(context);
    var result = new T();
    result.MergeFrom(responseBody.ToArray());
    return result;
}
