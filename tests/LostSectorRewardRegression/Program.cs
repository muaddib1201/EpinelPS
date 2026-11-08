using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer;
using EpinelPS.Models;
using EpinelPS.Networking;
using EpinelPS.Services;
using EpinelPS.Utils;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

const ulong account = 99201;
const int cubeId = 1000301;
const string finalChest = "7fe0b093-bdab-454b-803b-e9885071af24";
var liveRoot = args.FirstOrDefault(a => a.StartsWith("--probe-live-root="));
if (liveRoot != null)
{
    try { await ProbeLive(liveRoot["--probe-live-root=".Length..]); return 0; }
    catch (Exception ex) { Console.Error.WriteLine($"FAIL read-only live probe: {ex}"); return 1; }
}
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await data.Parse();
Console.WriteLine($"Static event missions loaded: {data.EventMissionListTable.Count}; static sectors: {data.LostSector.Count}");
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "static-mission-audit.json"), JsonConvert.SerializeObject(data.EventMissionListTable.Values, Formatting.Indented));
var savePath = Path.Combine(AppContext.BaseDirectory, "db.json");
bool verifySaved = args.Contains("--verify-saved");
var info = verifySaved ? JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(savePath))! : new CoreInfo();
var infoProperty = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
infoProperty.SetValue(null, info);
var conn = $"Data Source={Path.Combine(AppContext.BaseDirectory, "lost-sector-reward.sqlite")}";
GameContext.InitializeOptions(conn, "sqlite");
if (!verifySaved)
{
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
    db.Users.Add(new GameUser { ID = account, Nickname = "LostSectorFixture", LastAction = DateTime.UtcNow });
    db.SaveChanges();
    info.Users.Add(new User { ID = account, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow,
        LostSectorData = { [1] = new LostSectorData { IsOpen = true, IsPlaying = true } } });
}
var user = info.Users.Single(u => u.ID == account);
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.AllowSynchronousIO = true; });
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(conn));
builder.Services.AddControllers(o => o.OutputFormatters.Insert(0, new ProtobufOutputFormatter()))
    .AddApplicationPart(typeof(LobbyHandler).Assembly);
await using var app = builder.Build();
app.Use(async (context, next) => {
    context.Items["UserID"] = ulong.TryParse(context.Request.Headers["X-Test-User"], out var testUser) ? testUser : account;
    await next();
});
app.MapControllers();
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/v1/") };
int passed = 0, failed = 0;

if (verifySaved)
{
    await Test("fresh process retains the single level-one final cube and claimed chest", async () => {
        var cube = user.Items.Single(x => x.ItemType == cubeId);
        Check(cube.Level == 1 && cube.Count == 1, "saved cube level/count");
        Check(user.LostSectorData[1].ReceivedFinalReward && user.LostSectorData[1].Objects.ContainsKey(finalChest), "saved chest progress");
        var inventory = await Send<ResGetInventoryData>("inventory/get", new ReqGetInventoryData());
        Check(inventory.Items.Single(x => x.Isn == cube.Isn).Lv == 1, "restart inventory level");
        Check(inventory.HarmonyCubes.Single(x => x.Isn == cube.Isn).Lv == 1, "restart cube level");
    });
    await Test("fresh process retains exactly one Lost Sector clear and no reward repair payout", async () => {
        var wallet = JsonConvert.SerializeObject(user.Currency);
        var items = JsonConvert.SerializeObject(user.Items);
        RecoverStartup();
        var sync = await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger());
        Check(sync.Triggers.Count(t => t.Trigger == 53 && t.ConditionId == 1 && t.UserValue == 1) == 1, "clear lost or duplicated after restart");
        Check(wallet == JsonConvert.SerializeObject(user.Currency) && items == JsonConvert.SerializeObject(user.Items), "restart recovery paid rewards");
        using var db = GameContext.CreateNew();
        Check(db.Triggers.Count(t => t.UserId == 99220 && t.Type == Trigger.LostSectorClear) == 1, "retried notification lost after restart");
    });
}
else
{
    await Test("an opened sector cannot satisfy the actual Day By Day clear condition", async () => {
        foreach (var row in data.EventMissionListTable.Values.Where(m => (m.Id >= 200010200 && m.Id < 200010300) || m.Trigger == Trigger.LostSectorClear))
            Console.WriteLine($"Mission definition: id={row.Id}, trigger={(int)row.Trigger}, condition={row.ConditionId}, target={row.ConditionValue}, name={row.NameLocalkey}");
        var mission = data.EventMissionListTable.Values.Single(m => m.Id >= 200010200 && m.Id < 200010300 && m.Trigger == Trigger.LostSectorClear);
        Console.WriteLine($"Day By Day condition: mission={mission.Id}, trigger={(int)mission.Trigger}, sector={mission.ConditionId}, target={mission.ConditionValue}");
        Check(mission.ConditionId == 1 && mission.ConditionValue == 1, "static Day By Day condition changed");
        var sync = await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger());
        Check(sync.Triggers.All(t => t.Trigger != 53), "opening a sector invented a clear");
    });
    await Test("new cube reward carries a valid level through protobuf before inventory refresh", () => {
        var fixture = new User { ID = 99202 };
        var reward = new NetRewardData();
        RewardUtils.AddSingleObject(fixture, ref reward, cubeId, RewardType.Item, 1);
        var saved = fixture.Items.Single();
        var wire = NetRewardData.Parser.ParseFrom(reward.ToByteArray()).UserItems.Single();
        Check(saved.Level == 1, "creation must start at level one");
        Check(wire.Lv == 1, $"new reward omitted Lv: wire={wire.Lv}, saved={saved.Level}");
        Check(wire.Isn == saved.Isn && wire.Tid == cubeId && wire.Count == 1, "reward identity/count");
        Check(data.ItemHarmonyCubeLevelTable.Values.Any(x => x.LevelEnhanceId == data.ItemHarmonyCubeTable[cubeId].LevelEnhanceId && x.Level == wire.Lv), "client upgrade lookup has no matching level row");
        return Task.CompletedTask;
    });
    await Test("real Lost Sector 1 final chest returns level one with its original payout", async () => {
        var map = data.MapData[data.LostSector[1].FieldId];
        var chest = map.ItemSpawner.Single(x => x.PositionId == finalChest);
        Check(data.FieldItems[chest.ItemId].IsFinalReward, "fixture must use the final chest");
        var response = await Send<ResObtainLostSectorItem>("lostsector/obtainitem", new ReqObtainLostSectorItem {
            SectorId = 1, Object = new NetLostSectorFieldObject { PositionId = finalChest, Type = 1, ActionAt = DateTime.UtcNow.Ticks }
        });
        var cube = response.Reward.UserItems.Single(x => x.Tid == cubeId);
        Check(cube.Lv == 1, $"final chest sent cube level {cube.Lv}");
        Check(response.BoxCount == 1 && response.Reward.Item.Single(x => x.Tid == cubeId).Count == 1, "chest/cube counts changed");
        Check(response.Reward.Currency.Single(x => x.Type == 3000).Value == 100 && user.GetCurrencyVal((CurrencyType)3000) == 100, "original final chest currency changed");
        Check(user.LostSectorData[1].ReceivedFinalReward && user.Items.Single(x => x.Isn == cube.Isn).Level == 1, "chest persistence");
    });
    await Test("a real final chest delivers the missing Day By Day clear notification", async () => {
        using var db = GameContext.CreateNew();
        Check(db.Triggers.Count(t => t.UserId == account && t.Type == Trigger.LostSectorClear && t.ConditionId == 1 && t.Value == 1) == 1,
            "final chest did not record LostSectorClear(53), sector 1, value 1");
        var sync = await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger());
        Check(sync.Triggers.Count(t => t.Trigger == 53 && t.ConditionId == 1 && t.UserValue == 1) == 1, "wire mission progress missing");
    });
    await Test("inventory refresh returns the saved level-one cube without another payout", async () => {
        var cube = user.Items.Single(x => x.ItemType == cubeId);
        var inventory = await Send<ResGetInventoryData>("inventory/get", new ReqGetInventoryData());
        var harmony = await Send<ResGetHarmonyCube>("inventory/getharmonycube", new ReqGetHarmonyCube());
        Check(inventory.Items.Single(x => x.Isn == cube.Isn).Lv == 1, "generic inventory level");
        Check(inventory.HarmonyCubes.Single(x => x.Isn == cube.Isn).Lv == 1 && harmony.HarmonyCubes.Single(x => x.Isn == cube.Isn).Lv == 1, "cube inventory levels");
        Check(user.Items.Count(x => x.ItemType == cubeId) == 1 && cube.Count == 1, "inventory read reissued cube");
    });
    await Test("ordinary level-zero item creation and stacking keep their amounts", () => {
        const int ordinaryItem = 9430003;
        var fixture = new User { ID = 99203 };
        var reward = new NetRewardData();
        RewardUtils.AddSingleObject(fixture, ref reward, ordinaryItem, RewardType.Item, 2);
        var first = NetRewardData.Parser.ParseFrom(reward.ToByteArray()).UserItems.Single();
        Check(first.Lv == 0 && first.Count == 2, "ordinary creation level/count");
        reward = new NetRewardData();
        RewardUtils.AddSingleObject(fixture, ref reward, ordinaryItem, RewardType.Item, 3);
        var second = NetRewardData.Parser.ParseFrom(reward.ToByteArray()).UserItems.Single();
        Check(second.Lv == 0 && second.Count == 5 && second.Isn == first.Isn && fixture.Items.Count == 1, "ordinary stacking changed");
        return Task.CompletedTask;
    });
    await Test("legacy final-chest evidence repairs once across repeated startup and sync without rewards", async () => {
        var legacy = Fixture(99210, new LostSectorData { IsOpen = true, ReceivedFinalReward = true, CompletedPerfectly = true, ObtainedRewards = 3 });
        var before = RewardState(legacy);
        RecoverStartup();
        RecoverStartup();
        var first = await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger(), legacy.ID);
        await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger(), legacy.ID);
        Check(first.Triggers.Count(t => t.Trigger == 53 && t.ConditionId == 1 && t.UserValue == 1) == 1, "legacy clear not recovered");
        using var db = GameContext.CreateNew();
        Check(db.Triggers.Count(t => t.UserId == legacy.ID && t.Type == Trigger.LostSectorClear) == 1, "startup/sync duplicated the clear");
        Check(RewardState(legacy) == before, "legacy repair changed rewards, event claims or exploration");
    });
    await Test("an already delivered clear is preserved while a second completed sector is recovered", async () => {
        var legacy = Fixture(99211, new LostSectorData { ReceivedFinalReward = true });
        legacy.LostSectorData[2] = new LostSectorData { ReceivedFinalReward = true };
        long id = legacy.AddTrigger(Trigger.LostSectorClear, 1, 1).Id;
        await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger(), legacy.ID);
        using var db = GameContext.CreateNew();
        Check(db.Triggers.Count(t => t.UserId == legacy.ID && t.Type == Trigger.LostSectorClear) == 2, "per-sector recovery count incorrect");
        Check(db.Triggers.Any(t => t.Id == id && t.ConditionId == 1) && db.Triggers.Any(t => t.UserId == legacy.ID && t.Type == Trigger.LostSectorClear && t.ConditionId == 2),
            "existing event changed or sector 2 was not recovered");
    });
    await Test("partial battles, perfect reward alone and unknown sectors cannot fabricate clears", async () => {
        var partial = Fixture(99212, new LostSectorData { IsOpen = true, IsPlaying = true, CompletedPerfectly = true });
        foreach (var stage in data.MapData[data.LostSector[1].FieldId].StageSpawner)
            partial.LostSectorData[1].ClearedStages[stage.PositionId] = stage.StageId;
        partial.LostSectorData[int.MaxValue] = new LostSectorData { ReceivedFinalReward = true };
        RecoverStartup();
        var sync = await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger(), partial.ID);
        Check(sync.Triggers.All(t => t.Trigger != 53), "unproven or unknown sector counted as a clear");
    });
    await Test("a failed JSON commit cannot leak an uncommitted mission notification", async () => {
        var legacy = Fixture(99213, new LostSectorData { ReceivedFinalReward = true });
        infoProperty.DeclaringType!.GetMethod("Save")!.Invoke(null, null);
        string temp = savePath + ".tmp";
        Directory.CreateDirectory(temp);
        bool rejected = false;
        try { await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger(), legacy.ID); }
        catch { rejected = true; }
        finally { Directory.Delete(temp); }
        Check(rejected, "blocked JSON commit was not rejected");
        using (var db = GameContext.CreateNew())
            Check(!db.Triggers.Any(t => t.UserId == legacy.ID && t.Type == Trigger.LostSectorClear), "failed commit leaked a clear to SQLite");
        Check(legacy.PendingMissionTriggers.Count == 0, "failed commit left an uncommitted outbox entry");
        var sync = await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger(), legacy.ID);
        Check(sync.Triggers.Count(t => t.Trigger == 53 && t.ConditionId == 1) == 1, "retry did not recover the committed legacy clear");
    });
    await Test("SQLite failure persists the new-clear outbox and retry delivers it once", async () => {
        var fixture = Fixture(99220, new LostSectorData { IsOpen = true, IsPlaying = true });
        using (var db = GameContext.CreateNew())
            db.Database.ExecuteSqlRaw("CREATE TRIGGER reject_lost_clear BEFORE INSERT ON Triggers WHEN NEW.Type=53 BEGIN SELECT RAISE(FAIL, 'fixture unavailable'); END;");
        try
        {
            await Send<ResObtainLostSectorItem>("lostsector/obtainitem", new ReqObtainLostSectorItem {
                SectorId = 1, Object = new NetLostSectorFieldObject { PositionId = finalChest, Type = 1 }
            }, fixture.ID);
            var persisted = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(savePath))!.Users.Single(u => u.ID == fixture.ID);
            Check(persisted.PendingMissionTriggers.Count(t => t.Type == Trigger.LostSectorClear && t.ConditionId == 1) == 1,
                "SQLite failure lost the durable notification");
            await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger(), fixture.ID);
            Check(fixture.PendingMissionTriggers.Count(t => t.Type == Trigger.LostSectorClear) == 1, "queued retry added another clear");
        }
        finally
        {
            using var db = GameContext.CreateNew();
            db.Database.ExecuteSqlRaw("DROP TRIGGER reject_lost_clear;");
        }
        string before = RewardState(fixture);
        var result = await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger(), fixture.ID);
        await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger(), fixture.ID);
        Check(result.Triggers.Count(t => t.Trigger == 53 && t.ConditionId == 1) == 1 && fixture.PendingMissionTriggers.Count == 0,
            "queued clear not drained exactly once");
        Check(before == RewardState(fixture), "retry paid final chest again");
    });
}
await app.StopAsync();
Console.WriteLine($"Lost Sector reward regression: {passed}/{passed + failed} passed");
return failed == 0 ? 0 : 1;

async Task<T> Send<T>(string route, IMessage request, ulong? owner = null) where T : IMessage<T>, new()
{
    using var message = new HttpRequestMessage(HttpMethod.Post, route);
    if (owner.HasValue) message.Headers.Add("X-Test-User", owner.Value.ToString());
    message.Content = new ByteArrayContent(request.ToByteArray());
    message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    using var response = await client.SendAsync(message);
    Check(response.StatusCode == HttpStatusCode.OK, $"{route} returned {(int)response.StatusCode}");
    var result = new T(); result.MergeFrom(await response.Content.ReadAsByteArrayAsync()); return result;
}
User Fixture(ulong id, LostSectorData sector)
{
    var fixture = new User { ID = id, ShopMissionHistoryRepaired = true, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow,
        LostSectorData = { [1] = sector } };
    info.Users.Add(fixture);
    using var db = GameContext.CreateNew();
    db.Users.Add(new GameUser { ID = id, Nickname = "LostMission" + id });
    db.SaveChanges();
    return fixture;
}
void RecoverStartup() => typeof(User).Assembly.GetType("EpinelPS.LobbyServer.Mission.MissionProgressDelivery")!
    .GetMethod("RecoverAll", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [DateTime.UtcNow]);
string RewardState(User fixture) => JsonConvert.SerializeObject(new { fixture.Currency, fixture.Items, fixture.EventMissionInfo, fixture.LostSectorData });
void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}

static async Task ProbeLive(string liveWorkspace)
{
    const ulong player = 1552239268;
    var saveFile = Path.Combine(liveWorkspace, "Server and Server selector/db.json");
    var before = JObject.Parse(File.ReadAllText(saveFile));
    var savedUser = before["Users"]!.Single(u => u.Value<ulong>("ID") == player)!;
    if (savedUser["ArenaHarmonyCubeAssignments"]?.Type != JTokenType.Object)
        throw new Exception("Read-only probe requires the existing arena cube map; do not migrate live data.");
    var savedCube = savedUser["Items"]!.Single(i => i.Value<int>("ItemType") == 1000301)!;
    long isn = savedCube.Value<long>("Isn");
    string auth = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
        .WithKey(Convert.FromBase64String(before.Value<string>("LauncherTokenKey")!), Encryption.SymmetricKey)
        .AddClaim("userId", player).IssuedAt(DateTime.UtcNow).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator })
        { BaseAddress = new Uri("https://127.0.0.1/v1/"), Timeout = TimeSpan.FromSeconds(15) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);
    var inventory = await Read<ResGetInventoryData>("inventory/get", new ReqGetInventoryData());
    var generic = inventory.Items.Single(i => i.Isn == isn);
    var cube = inventory.HarmonyCubes.Single(i => i.Isn == isn);
    if (generic.Lv != 1 || cube.Lv != 1 || generic.Count != savedCube.Value<int>("Count"))
        throw new Exception("Live cube level or count differs from the saved level-one item.");
    var sector = (await Read<ResGetLostSectorData>("lostsector/get", new ReqGetLostSectorData())).LostSector.Single(s => s.SectorId == 1);
    var savedSector = savedUser["LostSectorData"]!["1"]!;
    if (sector.CurrentClearStageCount != ((JObject)savedSector["ClearedStages"]!).Count ||
        sector.RewardCount != savedSector.Value<int>("ObtainedRewards") ||
        sector.IsFinalReward != savedSector.Value<bool>("ReceivedFinalReward") ||
        sector.IsPerfectReward != savedSector.Value<bool>("CompletedPerfectly"))
        throw new Exception("Live Lost Sector progress differs from its saved state.");
    var after = JObject.Parse(File.ReadAllText(saveFile));
    foreach (var human in before["Users"]!.Where(u => u["ManagedNpc"] == null || u["ManagedNpc"]!.Type == JTokenType.Null))
        if (!JToken.DeepEquals(human, after["Users"]!.Single(u => u.Value<ulong>("ID") == human.Value<ulong>("ID"))))
            throw new Exception("A human account changed during the read-only probe.");
    Console.WriteLine($"PASS live inventory: cube {generic.Tid}, level {generic.Lv}, count {generic.Count}");
    Console.WriteLine($"PASS live Lost Sector 1: {sector.CurrentClearStageCount}/{sector.MaxClearStageCount} stages, {sector.RewardCount} chests, final={sector.IsFinalReward}; no reward submitted");
    Console.WriteLine("PASS all human account fields preserved by the read-only probe");

    async Task<T> Read<T>(string route, IMessage request) where T : IMessage<T>, new()
    {
        using var body = new ByteArrayContent(request.ToByteArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
        using var response = await http.PostAsync(route, body);
        response.EnsureSuccessStatusCode();
        var message = new T(); message.MergeFrom(await response.Content.ReadAsByteArrayAsync()); return message;
    }
}
