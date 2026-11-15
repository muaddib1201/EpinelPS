using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

const int eventId = 40125, stage = 112520111, ticket = 7070124, rewardItem = 9140001;
var root = Environment.GetEnvironmentVariable("NIKKE_WORKSPACE_ROOT") ?? throw new Exception("Use the isolated test workflow");
var pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await Load("EventStoryTable", data.EventStoryTable, r => r.Id);
await Load("EventDungeonTable", data.EventDungeonTable, r => r.Id);
await Load("EventDungeonDifficultTable", data.EventDungeonDifficultTable, r => r.Id);
await Load("EventDungeonStageTable", data.EventDungeonStageTable, r => r.Id);
await Load("EventDungeonSpotBattleTable", data.EventDungeonSpotBattleTable, r => r.Id);
await Load("AutoChargeTable", data.AutoChargeTable, r => r.Id);
await Load("RewardTable", data.RewardDataRecords, r => r.Id);
var savePath = Path.Combine(AppContext.BaseDirectory, "db.json");
var store = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb", true)!.GetProperty("Instance")!;
var info = new CoreInfo { BondRewardLevelsMigrated = true };
store.SetValue(null, info);
GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "story-tickets.sqlite")}", "sqlite");
using var db = GameContext.CreateNew();
db.Database.EnsureDeleted();
db.Database.EnsureCreated();
using var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();

var user = NewUser(1, 0, 4);
var first = await Sweep(user, 4, 1);
Check(first.RemainTicket == 0 && Items(user, ticket) == 0, "A four-ticket sweep must return the debited balance, including zero");
Check(first.Reward.UserItems.Single(i => i.Tid == ticket).Count == 0 && Items(user, rewardItem) == 16,
    "The client must receive the final ticket stack and four clears' rewards");
info = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(savePath))!;
store.SetValue(null, info);
user = info.Users.Single();
var retry = await Sweep(user, 4, 1);
Check(retry.RemainTicket == 0 && retry.Reward.Item.Any(i => i.Tid == rewardItem && i.Count == 16) &&
    Items(user, rewardItem) == 16 && (await Get(user)).RemainTicket == 0, "A lost response must be retryable after reload");
var before = db.Triggers.AsNoTracking().Count(t => t.UserId == user.ID);
await Reject(() => Sweep(user, 4, 2));
Check(Items(user, rewardItem) == 16 && db.Triggers.AsNoTracking().Count(t => t.UserId == user.ID) == before,
    "An unaffordable sweep must leave rewards and mission credit untouched");
Console.WriteLine("PASS paid-ticket settlement, empty wallet, durable retries and insufficient funds");

var mixed = NewUser(2, 1, 1, 3);
Check((await Get(mixed)).RemainTicket == 5, "The displayed wallet must include every ticket stack");
var split = await Sweep(mixed, 4, 3);
Check(split.RemainTicket == 1 && mixed.EventInfo[eventId].FreeTicket == 0 &&
    mixed.Items.Where(i => i.ItemType == ticket).Select(i => i.Count).SequenceEqual([0, 1]),
    "Spend the free allowance before debiting as many inventory stacks as needed");
Check(split.Reward.UserItems.Where(i => i.Tid == ticket).Select(i => i.Count).SequenceEqual([0, 1]),
    "Every debited stack must be returned at its final count");
await Reject(() => Sweep(mixed, 2, 4));
Check((await Get(mixed)).RemainTicket == 1 && Items(mixed, rewardItem) == 16, "Do not partially settle an unaffordable batch");
Console.WriteLine("PASS mixed free and split inventory tickets, final stack counts and all-or-nothing batches");

var repeat = NewUser(3, 4);
var one = await Sweep(repeat, 2, 5);
var two = await Sweep(repeat, 2, 6);
Check(one.RemainTicket == 2 && two.RemainTicket == 0 && Items(repeat, rewardItem) == 16,
    "Two independently initiated sweeps with the same stage and quantity must each settle");
var concurrent = NewUser(4, 2);
await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => Sweep(concurrent, 2, 7))));
Check((await Get(concurrent)).RemainTicket == 0 && Items(concurrent, rewardItem) == 8 &&
    db.Triggers.AsNoTracking().Count(t => t.UserId == concurrent.ID && t.Type == Trigger.EventDungeonStageClear) == 1,
    "Concurrent delivery of one settlement must debit and grant once");
Console.WriteLine("PASS independent repeat sweeps and concurrent delivery");

var manual = NewUser(5, 0, 2);
await Enter(manual);
Check((await Clear(manual, 2, 101)).RemainTicket == 2 && Items(manual, rewardItem) == 0, "A retreat must preserve tickets and rewards");
await Enter(manual);
var win = await Clear(manual, 1, 102);
var sameWin = await Clear(manual, 1, 102);
Check(win.RemainTicket == 1 && sameWin.RemainTicket == 1 && Items(manual, rewardItem) == 4, "A manual win must settle once");
await Enter(manual);
Check((await Clear(manual, 1, 103)).RemainTicket == 0 && Items(manual, rewardItem) == 8, "A second manual run must spend the last ticket");
await Reject(() => Enter(manual));
await Reject(() => Clear(manual, 1, 104));
Check(Items(manual, rewardItem) == 8, "An empty wallet must not generate a manual settlement");
Console.WriteLine("PASS manual repeats, retreat and entry balance");

var daily = NewUser(6, 0, 1);
daily.EventInfo[eventId].LastDay = 20250101;
Check((await Sweep(daily, 2, 8)).RemainTicket == 4 && (await Get(daily)).RemainTicket == 4 && Items(daily, ticket) == 1,
    "Settlement must refresh the daily allowance once even without reopening the dungeon");
var rollback = NewUser(7, 1);
before = db.Triggers.AsNoTracking().Count(t => t.UserId == rollback.ID);
var blocked = savePath + ".tmp";
Directory.CreateDirectory(blocked);
try
{
    try { await Sweep(rollback, 1, 9); throw new Exception("The save failure was not reported"); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
}
finally { Directory.Delete(blocked); }
Check(rollback.EventInfo[eventId].FreeTicket == 1 && Items(rollback, rewardItem) == 0 &&
    db.Triggers.AsNoTracking().Count(t => t.UserId == rollback.ID) == before, "A failed save must roll back the whole settlement");
Check((await Sweep(rollback, 1, 9)).RemainTicket == 0 && Items(rollback, rewardItem) == 4, "Retry after a failed save must commit exactly once");
Console.WriteLine("PASS daily allowance, failed save rollback and retry");

async Task Load<T>(string table, Dictionary<int, T> target, Func<T, int> id) where T : new()
{
    foreach (var row in await data.LoadZip<T>(table + ".json", new Progress<double>())) target.Add(id(row), row);
}
User NewUser(ulong id, int free, params int[] paid)
{
    var u = new User { ID = id };
    u.EventInfo[eventId] = new EventData { LastDay = u.GetDateDay(), FreeTicket = free, ClearedStages = [stage], LastStage = stage };
    for (int i = 0; i < paid.Length; i++) u.Items.Add(new DbItemData { Isn = 100 + i, ItemType = ticket, Count = paid[i] });
    info.Users.Add(u);
    db.Users.Add(new GameUser { ID = id });
    db.SaveChanges();
    return u;
}
int Items(User u, int id) => u.Items.Where(i => i.ItemType == id).Sum(i => i.Count);
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
async Task Reject(Func<Task> action)
{
    try { await action(); } catch (BadHttpRequestException e) when (e.StatusCode == 409) { return; }
    throw new Exception("Expected the story battle to reject the unaffordable operation");
}
Task<ResStoryDungeonEventData> Get(User u) => Call<ResStoryDungeonEventData>(u, "get", new ReqStoryDungeonEventData { EventId = eventId });
Task<ResEnterEventStage> Enter(User u) => Call<ResEnterEventStage>(u, "enterstage", new ReqEnterEventStage { EventId = eventId, StageId = stage, TeamNumber = 1 });
Task<ResClearEventStage> Clear(User u, int result, long time) => Call<ResClearEventStage>(u, "clearstage",
    new ReqClearEventStage { EventId = eventId, StageId = stage, TeamNumber = 1, BattleResult = result, AntiCheatAdditionalInfo = new() { ClientLocalTime = time } });
Task<ResFastClearEventStage> Sweep(User u, int count, byte key) => Call<ResFastClearEventStage>(u, "fastclearstage",
    new ReqFastClearEventStage { EventId = eventId, StageId = stage, TeamNumber = 1, ClearCount = count }, key);
async Task<T> Call<T>(User u, string route, IMessage request, byte? nonce = null) where T : IMessage<T>, new()
{
    var context = new DefaultHttpContext { RequestServices = services };
    context.Items["UserID"] = u.ID;
    if (nonce.HasValue) context.Items["Nonce"] = new byte[] { nonce.Value };
    using var input = new MemoryStream(request.ToByteArray());
    using var output = new MemoryStream();
    context.Request.Body = input;
    context.Response.Body = output;
    var handler = (LobbyMessage)Activator.CreateInstance(LobbyHandler.Handlers["/event/storydungeon/" + route].GetType())!;
    await handler.HandleAsync(context);
    if (context.Response.StatusCode >= 400) throw new BadHttpRequestException("Story battle rejected", context.Response.StatusCode);
    var response = new T();
    response.MergeFrom(output.ToArray());
    return response;
}
