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
    context.Items["UserID"] = account;
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
}
else
{
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
}
await app.StopAsync();
Console.WriteLine($"Lost Sector reward regression: {passed}/{passed + failed} passed");
return failed == 0 ? 0 : 1;

async Task<T> Send<T>(string route, IMessage request) where T : IMessage<T>, new()
{
    using var message = new HttpRequestMessage(HttpMethod.Post, route);
    message.Content = new ByteArrayContent(request.ToByteArray());
    message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    using var response = await client.SendAsync(message);
    Check(response.StatusCode == HttpStatusCode.OK, $"{route} returned {(int)response.StatusCode}");
    var result = new T(); result.MergeFrom(await response.Content.ReadAsByteArrayAsync()); return result;
}
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
