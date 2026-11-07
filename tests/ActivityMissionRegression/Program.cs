using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer;
using EpinelPS.LobbyServer.Controllers;
using EpinelPS.LobbyServer.Shop;
using EpinelPS.Models;
using EpinelPS.Services;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
if (args.Contains("--probe-live-readonly")) return await LiveChecks.Run(root);
var pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
if (args.Contains("--preview-live-repair")) return MigrationPreview.Run(root);
string save = Path.Combine(AppContext.BaseDirectory, "db.json");
string connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "activity-mission.db")}";
GameContext.InitializeOptions(connection, "sqlite");
bool restarting = args.Contains("--verify-saved");
if (!restarting)
{
    var users = Enumerable.Range(850, 31).Select(id => new User
    {
        ID = (ulong)id, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow,
        Guild = new GuildData { guildId = id, MemberType = 1, JoinedAt = DateTime.UtcNow.AddDays(-10).Ticks },
        Currency = Enum.GetValues<CurrencyType>().Distinct().ToDictionary(t => t, t => 1_000_000_000L)
    }).ToList();
    File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo
    {
        DbVersion = 5, BondRewardLevelsMigrated = true, Users = users,
        Guilds = users.ToDictionary(u => (int)u.ID, u => new SavedGuild { Data = new NetGuildData { Gsn = (long)u.ID, Name = "Test" + u.ID, Grade = 1 } })
    }));
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
    db.Users.AddRange(users.Select(u => new GameUser { ID = u.ID, Nickname = "Test" + u.ID }));
    db.SaveChanges();
}
var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
CoreInfo State() => (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
User User(ulong id) => State().Users.Single(u => u.ID == id);
SavedGuild Guild(ulong id) => State().Guilds[(int)id];
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
int passed = 0, failed = 0;

if (restarting)
{
    await Test("a fresh process retains contributions and completed daily claims", async () =>
    {
        var before = Guild(850).Data.ActivityPoint;
        var req = new ReqObtainDailyMissionReward { TidList = { 10001 } };
        await Send<ResObtainDailyMissionReward>(850, "/mission/obtain/daily", req);
        Check(before == 10 && Guild(850).Data.ActivityPoint == 10, "restart paid the daily claim twice or lost activity");
        var hall = Hall(850);
        Check(hall.Members.Single().ActivityPointDaily == 10, "restart lost the member's day history");
    });
    await Test("a fresh process retains successful shop mission notifications", async () =>
    {
        var sync = await Send<ResSyncTrigger>(851, "/trigger/sync", new ReqSyncTrigger());
        Check(sync.Triggers.Count(t => t.Trigger == 11 && t.UserValue == 1) == 1, "shop event was lost or duplicated across processes");
    });
    Console.WriteLine($"RESULT {passed} passed, {failed} failed");
    return failed == 0 ? 0 : 1;
}

await Test("claiming a daily task credits the human Union and member", async () =>
{
    await Send<ResObtainDailyMissionReward>(850, "/mission/obtain/daily", new ReqObtainDailyMissionReward { TidList = { 10001 } });
    var hall = Hall(850);
    Check(hall.Guild.ActivityPoint == 10 && hall.Guild.Exp == 10, "claimed 10 daily points but Union activity/experience did not increase");
    Check(hall.Members.Single().ActivityPointDaily == 10 && hall.Members.Single().ActivityPointRecent == 10, "human member contribution is still zero");
});
await Test("a repeated daily reward claim cannot credit twice", async () =>
{
    await Send<ResObtainDailyMissionReward>(850, "/mission/obtain/daily", new ReqObtainDailyMissionReward { TidList = { 10001, 10001 } });
    Check(Guild(850).Data.ActivityPoint == 10 && User(850).ResetableData.DailyMissionPoints == 10, "repeated daily claim changed activity or points");
});
await Test("a successful single ordinary-shop purchase is delivered to mission sync", async () =>
{
    var product = NormalShopHelper.GetShopData(User(851), 1).List.First();
    long before = DateTime.UtcNow.Ticks;
    var response = await Send<ResShopBuyProduct>(851, "/shop/buy", new ReqShopBuyProduct { ShopCategory = 1, ShopProductTid = product.ProductId, Order = product.Order });
    Check(response.Result == ShopBuyProductResult.Success, "single purchase failed");
    var sync = await Send<ResSyncTrigger>(851, "/trigger/sync", new ReqSyncTrigger());
    var trigger = sync.Triggers.SingleOrDefault(t => t.Trigger == 11);
    Check(trigger?.UserValue == 1 && trigger.ConditionId == 0, "successful ordinary shop purchase has no MainShopBuy mission event");
    Check(trigger.CreatedAt >= before && trigger.CreatedAt <= DateTime.UtcNow.Ticks, "shop mission event uses the wrong game day");
});
await Test("a successful batch ordinary-shop purchase counts every purchased quantity", async () =>
{
    var products = NormalShopHelper.GetShopData(User(852), 1).List.Take(2).ToArray();
    var req = new ReqShopBuyMultipleProduct { ShopCategory = 1 };
    foreach (var p in products) req.Products.Add(new NetBuyProductRequestData { ShopProductTid = p.ProductId, Order = p.Order, Quantity = 1 });
    var response = await Send<ResShopBuyMultipleProduct>(852, "/shop/multiple-buy", req);
    Check(response.Result == ShopBuyProductResult.Success, "batch purchase failed");
    var sync = await Send<ResSyncTrigger>(852, "/trigger/sync", new ReqSyncTrigger());
    Check(sync.Triggers.Where(t => t.Trigger == 11).Sum(t => t.UserValue) == 2, "batch purchase did not report two ordinary-shop items");
});
await Test("a rejected ordinary-shop purchase cannot advance the mission", async () =>
{
    var response = await Send<ResShopBuyProduct>(853, "/shop/buy", new ReqShopBuyProduct { ShopCategory = 1, ShopProductTid = int.MaxValue, Quantity = 1 });
    var sync = await Send<ResSyncTrigger>(853, "/trigger/sync", new ReqSyncTrigger());
    Check(response.Result != ShopBuyProductResult.Success && sync.Triggers.All(t => t.Trigger != 11), "failed purchase contributed task progress");
});
await Test("other shop categories cannot satisfy the ordinary-shop daily task", async () =>
{
    var product = NormalShopHelper.GetShopData(User(854), 5).List.First();
    var response = await Send<ResShopBuyProduct>(854, "/shop/buy", new ReqShopBuyProduct { ShopCategory = 5, ShopProductTid = product.ProductId, Order = product.Order, Quantity = 1 });
    Check(response.Result == ShopBuyProductResult.Success, "non-normal purchase fixture failed");
    var sync = await Send<ResSyncTrigger>(854, "/trigger/sync", new ReqSyncTrigger());
    Check(sync.Triggers.All(t => t.Trigger != 11), "Arena shop incorrectly counted as the normal shop");
});
await Test("daily Union contribution stops at 100 while mission points keep their own count", async () =>
{
    await Send<ResObtainDailyMissionReward>(855, "/mission/obtain/daily", new ReqObtainDailyMissionReward
    { TidList = { 10001, 10002, 10003, 10004, 10005, 10006, 10007, 10008, 10009, 10010, 10011, 10012, 10018, 10019 } });
    Check(User(855).ResetableData.DailyMissionPoints == 170 && Guild(855).Data.ActivityPoint == 100, "daily contribution did not stop at 100");
    Check(Hall(855).Members.Single().ActivityPointRecent == 100, "member daily history exceeded its limit");
});

await Test("rolling seven-day activity expires the oldest game day", () =>
{
    var at = new DateTime(2026, 11, 1, 21, 0, 0, DateTimeKind.Utc);
    Activity("Record", User(856), 10, at);
    Activity("Record", User(856), 20, at.AddDays(1));
    Activity("Record", User(856), 30, at.AddDays(6));
    Check(Guild(856).Data.ActivityPointDaily == 30 && Guild(856).Data.ActivityPointRecent == 60, "seventh day excluded valid activity");
    Activity("Refresh", Guild(856), at.AddDays(7));
    Check(Guild(856).Data.ActivityPointDaily == 0 && Guild(856).Data.ActivityPointRecent == 50 && Guild(856).Data.ActivityPoint == 60,
        "daily rollover reset the lifetime total or kept an eighth day");
    return Task.CompletedTask;
});
await Test("the configured UTC reset boundary permits a new day's contribution", () =>
{
    var at = new DateTime(2026, 11, 6, 19, 59, 59, DateTimeKind.Utc);
    Activity("Record", User(857), 150, at);
    Activity("Record", User(857), 50, at.AddSeconds(1));
    Check(Guild(857).Data.ActivityPoint == 150 && Guild(857).Data.ActivityPointDaily == 50 && Guild(857).Data.ActivityPointRecent == 150,
        "activity used midnight instead of the 20:00 UTC reset boundary");
    return Task.CompletedTask;
});
await Test("contribution crosses the level threshold and retains excess experience", () =>
{
    Guild(858).Data.Exp = 8990;
    Activity("Record", User(858), 20, DateTime.UtcNow);
    Check(Guild(858).Data.Grade == 2 && Guild(858).Data.Exp == 10 && Guild(858).Data.ActivityPoint == 20, "Union upgrade lost excess experience");
    return Task.CompletedTask;
});
await Test("daily claims outside a Union do not create phantom contribution", async () =>
{
    User(859).Guild = new();
    await Send<ResObtainDailyMissionReward>(859, "/mission/obtain/daily", new ReqObtainDailyMissionReward { TidList = { 10001 } });
    Check(User(859).ResetableData.DailyMissionPoints == 10 && Guild(859).Data.ActivityPoint == 0, "guildless account cannot claim or received phantom contribution");
});
await Test("proven legacy claimed points are recovered once without paying rewards again", () =>
{
    User(860).ResetableData.DailyMissionPoints = 100;
    var wallet = JsonConvert.SerializeObject(User(860).Currency);
    Activity("EnsureMember", User(860), DateTime.UtcNow);
    Activity("EnsureMember", User(860), DateTime.UtcNow);
    Activity("Record", User(860), 10, DateTime.UtcNow);
    Check(Guild(860).Data.ActivityPoint == 100 && JsonConvert.SerializeObject(User(860).Currency) == wallet,
        "legacy activity was not recovered exactly once or regranted rewards");
    return Task.CompletedTask;
});
await Test("legacy NPC activity is not counted twice and new activity preserves the human ledger", () =>
{
    var now = DateTime.UtcNow;
    int day = int.Parse(now.AddHours(-20).ToString("yyyyMMdd"));
    User(861).ManagedNpc = new ManagedNpcState { Day = day, ActivityToday = 30 };
    Guild(861).NpcActivityDay = day;
    Guild(861).Data.ActivityPoint = Guild(861).Data.Exp = Guild(861).Data.ActivityPointDaily = 30;
    Activity("EnsureMember", User(861), now);
    Activity("Refresh", Guild(861), now);
    Check(Guild(861).Data.ActivityPoint == 30 && (int)Activity("Daily", User(861), now)! == 30, "legacy NPC activity was duplicated");
    Activity("Record", User(861), 20, now.AddDays(1));
    Check(Guild(861).Data.ActivityPoint == 50 && Guild(861).Data.ActivityPointRecent == 50, "NPC rollover lost seven-day activity");
    return Task.CompletedTask;
});
await Test("a temporary SQLite failure delivers the committed purchase notification later once", async () =>
{
    var product = NormalShopHelper.GetShopData(User(862), 1).List.First();
    using (var db = GameContext.CreateNew())
        db.Database.ExecuteSqlRaw("CREATE TRIGGER reject_shop_event BEFORE INSERT ON Triggers WHEN NEW.Type = 11 BEGIN SELECT RAISE(FAIL, 'temporary notification failure'); END;");
    try
    {
        var r = await Send<ResShopBuyProduct>(862, "/shop/buy", new ReqShopBuyProduct { ShopCategory = 1, Order = product.Order, ShopProductTid = product.ProductId, Quantity = 1 });
        Check(r.Result == ShopBuyProductResult.Success && User(862).PendingMissionTriggers.Any(t => t.Type == Trigger.MainShopBuy), "committed purchase lost its pending event");
    }
    finally
    {
        using var db = GameContext.CreateNew();
        db.Database.ExecuteSqlRaw("DROP TRIGGER reject_shop_event;");
    }
    var first = await Send<ResSyncTrigger>(862, "/trigger/sync", new ReqSyncTrigger());
    var second = await Send<ResSyncTrigger>(862, "/trigger/sync", new ReqSyncTrigger());
    Check(first.Triggers.Count(t => t.Trigger == 11) == 1 && second.Triggers.Count(t => t.Trigger == 11) == 1 && User(862).PendingMissionTriggers.Count == 0,
        "outbox delivery duplicated the purchase or did not clear");
});
await Test("a JSON save failure rolls back daily claims, Union activity and event delivery", async () =>
{
    var temp = Path.Combine(AppContext.BaseDirectory, "db.json.tmp");
    Directory.CreateDirectory(temp);
    bool rejected = false;
    try
    {
        try { await Send<ResObtainDailyMissionReward>(863, "/mission/obtain/daily", new ReqObtainDailyMissionReward { TidList = { 10001 } }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
    }
    finally { Directory.Delete(temp); }
    using var db = GameContext.CreateNew();
    Check(rejected && !User(863).ResetableData.CompletedDailyMissions.Contains(10001) && User(863).ResetableData.DailyMissionPoints == 0 &&
        Guild(863).Data.ActivityPoint == 0 && !db.Triggers.Any(t => t.UserId == 863), "failed save leaked claims, contribution or notifications");
});
await Test("a rejected batch does not buy its valid first row or advance missions", async () =>
{
    var product = NormalShopHelper.GetShopData(User(864), 1).List.First();
    var req = new ReqShopBuyMultipleProduct { ShopCategory = 1, Products =
    {
        new NetBuyProductRequestData { ShopProductTid = product.ProductId, Order = product.Order, Quantity = 1 },
        new NetBuyProductRequestData { ShopProductTid = int.MaxValue, Quantity = 1 }
    } };
    var r = await Send<ResShopBuyMultipleProduct>(864, "/shop/multiple-buy", req);
    Check(r.Result != ShopBuyProductResult.Success && User(864).NormalShopStates[1].BuyCountsByOrder.Count == 0, "failed batch partially purchased");
    var sync = await Send<ResSyncTrigger>(864, "/trigger/sync", new ReqSyncTrigger());
    Check(sync.Triggers.All(t => t.Trigger != 11), "failed batch published progress");
});
await Test("a sold-out row cannot contribute a second purchase event", async () =>
{
    var product = NormalShopHelper.GetShopData(User(865), 1).List.First(p => data.ContentsShopProductTable[p.ProductId] is { IsLimit: true, BuyLimitCount: 1 });
    var req = new ReqShopBuyProduct { ShopCategory = 1, ShopProductTid = product.ProductId, Order = product.Order, Quantity = 1 };
    var first = await Send<ResShopBuyProduct>(865, "/shop/buy", req);
    var second = await Send<ResShopBuyProduct>(865, "/shop/buy", req);
    var sync = await Send<ResSyncTrigger>(865, "/trigger/sync", new ReqSyncTrigger());
    Check(first.Result == ShopBuyProductResult.Success && second.Result != ShopBuyProductResult.Success && sync.Triggers.Where(t => t.Trigger == 11).Sum(t => t.UserValue) == 1,
        "sold-out purchase contributed twice");
});
await Test("single purchase mission progress uses the requested quantity", async () =>
{
    var product = NormalShopHelper.GetShopData(User(866), 1).List.First(p => data.ContentsShopProductTable[p.ProductId] is { IsLimit: false } || data.ContentsShopProductTable[p.ProductId].BuyLimitCount >= 3);
    var r = await Send<ResShopBuyProduct>(866, "/shop/buy", new ReqShopBuyProduct { ShopCategory = 1, ShopProductTid = product.ProductId, Order = product.Order, Quantity = 3 });
    var sync = await Send<ResSyncTrigger>(866, "/trigger/sync", new ReqSyncTrigger());
    Check(r.Result == ShopBuyProductResult.Success && sync.Triggers.Where(t => t.Trigger == 11).Sum(t => t.UserValue) == 3, "purchase quantity was not preserved");
});
await Test("shop history recovery adds only missing proven progress without granting goods", () =>
{
    var now = DateTime.UtcNow;
    var start = now.AddHours(-20).Date.AddHours(20);
    User(867).NormalShopStates[1] = new NormalShopState { RenewAt = start.AddMinutes(1).Ticks, BuyCountsByOrder = new() { [1] = 5 } };
    User(867).AddTrigger(Trigger.MainShopBuy, 2);
    User(868).NormalShopStates[1] = new NormalShopState { RenewAt = start.AddHours(-1).Ticks, BuyCountsByOrder = new() { [1] = 9 } };
    var wallet = JsonConvert.SerializeObject(User(867).Currency);
    Delivery("RecoverAll", now);
    Delivery("RecoverAll", now);
    using var db = GameContext.CreateNew();
    Check(db.Triggers.Where(t => t.UserId == 867 && t.Type == Trigger.MainShopBuy).Sum(t => t.Value) == 5 &&
        JsonConvert.SerializeObject(User(867).Currency) == wallet, "repair duplicated progress or goods");
    Check(!db.Triggers.Any(t => t.UserId == 868 && t.Type == Trigger.MainShopBuy), "repair invented dates for ambiguous old purchases");
    return Task.CompletedTask;
});
await Test("legacy claims from the previous game day retain their date", () =>
{
    User(869).LastReset = DateTime.UtcNow.AddDays(-1);
    User(869).ResetableData.DailyMissionPoints = 100;
    Activity("EnsureMember", User(869), DateTime.UtcNow);
    Activity("Refresh", Guild(869), DateTime.UtcNow);
    Check(Guild(869).Data.ActivityPoint == 100 && Guild(869).Data.ActivityPointDaily == 0 && Guild(869).Data.ActivityPointRecent == 100,
        "legacy prior-day claims were credited as today's contribution");
    return Task.CompletedTask;
});
await Test("a failed commit cannot later publish notifications through a retained account reference", async () =>
{
    var original = User(875);
    var temp = Path.Combine(AppContext.BaseDirectory, "db.json.tmp");
    Directory.CreateDirectory(temp);
    try
    {
        try { await Send<ResObtainDailyMissionReward>(875, "/mission/obtain/daily", new ReqObtainDailyMissionReward { TidList = { 10001 } }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    finally { Directory.Delete(temp); }
    Delivery("Flush", original);
    using var db = GameContext.CreateNew();
    Check(ReferenceEquals(original, User(875)) && original.ResetableData.DailyMissionPoints == 0 && original.PendingMissionTriggers.Count == 0 &&
        !db.Triggers.Any(t => t.UserId == 875), "failed transaction left a stale account capable of leaking progress");
});
await Test("a claim after the daily boundary can earn the same task again", async () =>
{
    User(876).InfraCoreLvl = 17;
    User(876).LastReset = DateTime.UtcNow.AddDays(-1);
    User(876).ResetableData.CompletedDailyMissions.Add(10001);
    User(876).ResetableData.DailyMissionPoints = 10;
    await Send<ResObtainDailyMissionReward>(876, "/mission/obtain/daily", new ReqObtainDailyMissionReward { TidList = { 10001 } });
    var hall = Hall(876);
    Check(Guild(876).Data.ActivityPoint == 20 && hall.Members.Single().ActivityPointDaily == 10 &&
        hall.Members.Single().ActivityPointRecent == 20 && User(876).ResetableData.DailyMissionPoints == 10,
        "new-day claim was skipped or previous-day contribution lost its date");
});
await Test("moving an already-contributing NPC cannot transfer its previous contribution", () =>
{
    var now = DateTime.UtcNow;
    User(877).Guild.MemberType = 3;
    User(877).ManagedNpc = new ManagedNpcState { Day = int.Parse(now.AddHours(-20).ToString("yyyyMMdd")), LastActionUtc = now.AddMinutes(-5) };
    Activity("Record", User(877), 30, now.AddMinutes(-5));
    User(877).ManagedNpc!.ActivityToday = 30;
    ManagedNpcService.Update(877, true, 1, 878, now);
    var hall = Hall(878);
    Check(hall.Members.Single(m => m.Usn == 877).ActivityPointDaily == 0 && Guild(878).Data.ActivityPoint == 0 && Guild(877).Data.ActivityPoint == 30,
        "NPC member contribution appeared in a Union that never received it");
    return Task.CompletedTask;
});
await Test("a previously delivered outbox entry cannot duplicate its SQLite notification", () =>
{
    var user = User(879);
    var delivered = user.AddTrigger(Trigger.MainShopBuy, 2);
    user.PendingMissionTriggers.Add(new TriggerModelNew { UserId = user.ID, Type = delivered.Type, Value = delivered.Value,
        CreatedAt = delivered.CreatedAt, ConditionId = delivered.ConditionId });
    Delivery("Flush", user);
    using var db = GameContext.CreateNew();
    Check(db.Triggers.Count(t => t.UserId == 879 && t.Type == Trigger.MainShopBuy) == 1 && user.PendingMissionTriggers.Count == 0,
        "delivery repeated a notification committed before an interrupted acknowledgement");
    return Task.CompletedTask;
});
await Test("leaving and rejoining cannot reset the account's 100-point daily budget", async () =>
{
    User(880).Guild.MemberType = 3;
    User(880).userPointData.UserLevel = 10;
    await Send<ResObtainDailyMissionReward>(880, "/mission/obtain/daily", new ReqObtainDailyMissionReward
    { TidList = { 10001, 10004, 10005, 10009, 10010, 10012, 10018 } });
    using var db = GameContext.CreateNew();
    var controller = new GuildController(new FixedUser(User(880)), db);
    Check(controller.Leave(new ReqLeaveGuild { Gsn = 880 }).Value!.Result == LeaveGuildResult.Ok, "leave fixture failed");
    User(880).Guild.LeaveAt = DateTime.UtcNow.AddDays(-1).Ticks;
    Check(controller.Join(new ReqJoinGuild { Gsn = 880 }).Value!.Result == JoinGuildResult.Ok, "rejoin fixture failed");
    await Send<ResObtainDailyMissionReward>(880, "/mission/obtain/daily", new ReqObtainDailyMissionReward { TidList = { 10002, 10003, 10006, 10007 } });
    Check(Guild(880).Data.ActivityPoint == 100 && User(880).ResetableData.DailyMissionPoints == 140 && Hall(880).Members.Single().ActivityPointDaily == 0,
        "rejoining reset the daily cap or transferred the old membership contribution");
});
jsonDb.GetMethod("Save")!.Invoke(null, null);
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

object? Activity(string method, params object[] args) => typeof(User).Assembly.GetType("EpinelPS.LobbyServer.Guild.GuildActivity")!
    .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
object? Delivery(string method, params object[] args) => typeof(User).Assembly.GetType("EpinelPS.LobbyServer.Mission.MissionProgressDelivery")!
    .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
ResGetGuild Hall(ulong id)
{
    using var db = GameContext.CreateNew();
    var controller = new GuildController(new FixedUser(User(id)), db);
    return controller.Get(new ReqGetGuild { Gsn = (long)id }).Value!;
}
async Task<T> Send<T>(ulong id, string route, IMessage request) where T : IMessage<T>, new()
{
    using var scope = services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Items["UserID"] = id;
    ctx.Request.Path = "/v1" + route;
    ctx.Request.Body = new MemoryStream(request.ToByteArray());
    ctx.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    var response = new T();
    response.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray());
    return response;
}
async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}
sealed class FixedUser(User user) : IUserService
{
    public User? GetUser() => user;
}
