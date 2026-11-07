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
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

const ulong account = 99101, other = 99102;
const int cubeA = 910001, cubeB = 910002, restrictedCube = 910003;
const long firstCube = 2001, secondCube = 2002, thirdCube = 2003;
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
// Controlled table rows make slot/level and class expectations independent of shipped table revisions.
data.ItemHarmonyCubeTable[cubeA] = new() { Id = cubeA, Class = CharacterClassType.All, LevelEnhanceId = 991, ItemSubType = ItemSubType.HarmonyCube };
data.ItemHarmonyCubeTable[cubeB] = new() { Id = cubeB, Class = CharacterClassType.All, LevelEnhanceId = 991, ItemSubType = ItemSubType.HarmonyCube };
data.ItemHarmonyCubeTable[restrictedCube] = new() { Id = restrictedCube, Class = (CharacterClassType)999, LevelEnhanceId = 991, ItemSubType = ItemSubType.HarmonyCube };
data.ItemHarmonyCubeLevelTable[991001] = new() { Id = 991001, LevelEnhanceId = 991, Level = 1, Slot = 2 };
data.ItemHarmonyCubeLevelTable[991002] = new() { Id = 991002, LevelEnhanceId = 991, Level = 2, Slot = 3 };
var dbType = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var dbProperty = dbType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!;
var saveMethod = dbType.GetMethod("Save", BindingFlags.Static | BindingFlags.Public)!;
var reloadMethod = dbType.GetMethod("Reload", BindingFlags.Static | BindingFlags.Public)!;
var jsonPath = Path.Combine(AppContext.BaseDirectory, "db.json");
var conn = $"Data Source={Path.Combine(AppContext.BaseDirectory, "arena-cube-regression.sqlite")}";
GameContext.InitializeOptions(conn, "sqlite");
if (!args.Contains("--verify-saved"))
{
    File.WriteAllText(jsonPath, JsonConvert.SerializeObject(new CoreInfo { Users = [Fixture(account), Fixture(other)] }));
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
    db.Users.AddRange(new[] { account, other }.Select(id => new GameUser { ID = id, Nickname = $"Cube{id}", LastAction = DateTime.UtcNow }));
    db.SaveChanges();
}
var builder = WebApplication.CreateBuilder();
User? capturedUser = null;
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.AllowSynchronousIO = true; });
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserService>(provider => {
    var accessor = provider.GetRequiredService<IHttpContextAccessor>();
    return accessor.HttpContext!.Request.Headers.ContainsKey("Test-Stale")
        ? new CapturedUserService(capturedUser!) : new UserService(accessor);
});
builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(conn));
builder.Services.AddControllers(o => o.OutputFormatters.Insert(0, new ProtobufOutputFormatter()))
    .AddApplicationPart(typeof(LobbyHandler).Assembly);
await using var app = builder.Build();
app.Use(async (ctx, next) => {
    if (ulong.TryParse(ctx.Request.Headers["Test-User"], out var id)) ctx.Items["UserID"] = id;
    await next();
});
app.MapControllers();
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/v1/") };
int passed = 0;
var failures = new List<string>();
if (args.Contains("--verify-saved"))
{
    Check(Get().Single(x => x.Isn == secondCube).CsnList.SequenceEqual(new long[] { 1003 }), "arena assignment did not survive process restart");
    Check(Current().Items.Single(x => x.Isn == firstCube).CsnList.SequenceEqual(new long[] { 1001, 1002 }), "normal cube changed on restart");
    Console.WriteLine("PASS fresh process: independent arena assignments persisted");
    return 0;
}

await Test("arena wear HTTP route assigns a cube", async () => {
    Reset();
    var result = await Send<ResWearArenaHarmonyCube>("wear", Wear(secondCube, 1003));
    Check(result.HarmonyCubes.Single(x => x.Isn == secondCube).CsnList.SequenceEqual(new long[] { 1003 }), "arena wear did not return the new assignment");
});
await Test("arena clear HTTP route removes one character", async () => {
    Reset();
    var result = await Send<ResClearArenaHarmonyCube>("clear", new ReqClearArenaHarmonyCube { Isn = firstCube, Csn = 1001 });
    Check(result.HarmonyCube?.CsnList.SequenceEqual(new long[] { 1002 }) == true, "arena clear did not return the remaining character");
});
await Test("arena management HTTP route applies a batch", async () => {
    Reset();
    var result = await Send<ResManagementArenaHarmonyCube>("management", new ReqManagementArenaHarmonyCube {
        Isn = firstCube, Clears = { 1001 }, Wears = { new NetWearHarmonyCubeData { Csn = 1003 } }
    });
    Check(result.HarmonyCubes.Single(x => x.Isn == firstCube).CsnList.Order().SequenceEqual(new long[] { 1002, 1003 }), "arena batch did not apply its target state");
});
if (!args.Contains("--routes-only"))
{
    await Test("battle snapshot reads legacy cube assignments without writing or migrating", () => {
        Reset();
        var before = File.ReadAllText(jsonPath);
        using (File.Open(jsonPath + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var cubes = Snapshot();
            Check(cubes.Single(x => x.Isn == firstCube).CsnList.SequenceEqual(new long[] { 1001, 1002 }), "snapshot lost legacy assignments");
            Check(typeof(User).GetProperty("ArenaHarmonyCubeAssignments")!.GetValue(Current()) == null, "snapshot migrated persisted state");
        }
        Check(before == File.ReadAllText(jsonPath), "snapshot wrote database");
        Get().Single(x => x.Isn == firstCube).CsnList.Clear();
        Check(Snapshot().Single(x => x.Isn == firstCube).CsnList.Count == 2, "returned mutable snapshot changed saved assignments");
        return Task.CompletedTask;
    });
    await Test("legacy assignments migrate once from list and legacy field and persist", () => {
        Reset();
        var user = Current();
        user.Items.Single(x => x.Isn == firstCube).CsnList = [1001];
        user.Items.Single(x => x.Isn == firstCube).Csn = 1002;
        Check(Get().Single(x => x.Isn == firstCube).CsnList.SequenceEqual(new long[] { 1001, 1002 }), "legacy fields not merged");
        Reload();
        Current().Items.Single(x => x.Isn == firstCube).CsnList.Clear();
        Current().Items.Single(x => x.Isn == firstCube).Csn = 1004;
        Check(Get().Single(x => x.Isn == firstCube).CsnList.SequenceEqual(new long[] { 1001, 1002 }), "normal reassignment overwrote migrated arena mapping");
        return Task.CompletedTask;
    });
    await Test("arena equip moves a character between cubes without changing normal equipment", async () => {
        Reset();
        var normal = JsonConvert.SerializeObject(Current().Items);
        var result = await Send<ResWearArenaHarmonyCube>("wear", Wear(secondCube, 1001));
        Check(result.HarmonyCubes.Single(x => x.Isn == firstCube).CsnList.SequenceEqual(new long[] { 1002 }), "old arena cube still owns character");
        Check(result.HarmonyCubes.Single(x => x.Isn == secondCube).CsnList.SequenceEqual(new long[] { 1001 }), "new arena cube missing character");
        Check(normal == JsonConvert.SerializeObject(Current().Items), "normal cube equipment changed");
        Reload();
        Check(Get().Single(x => x.Isn == secondCube).CsnList.SequenceEqual(new long[] { 1001 }), "arena reassignment not saved");
    });
    await Test("full cube requires a swap and target assignment retries are idempotent", async () => {
        Reset();
        Get();
        await Reject("wear", Wear(firstCube, 1003), HttpStatusCode.BadRequest);
        await Send<ResWearArenaHarmonyCube>("wear", Wear(firstCube, 1003, 1001));
        await Send<ResWearArenaHarmonyCube>("wear", Wear(firstCube, 1003, 1001));
        Check(Get().Single(x => x.Isn == firstCube).CsnList.Order().SequenceEqual(new long[] { 1002, 1003 }), "swap/retry lost or duplicated assignment");
    });
    await Test("current inventory level controls slots and returned metadata", async () => {
        Reset();
        Get();
        Current().Items.Single(x => x.Isn == firstCube).Level = 2;
        await Send<ResWearArenaHarmonyCube>("wear", Wear(firstCube, 1003));
        var cube = Get().Single(x => x.Isn == firstCube);
        Check(cube.Lv == 2 && cube.Tid == cubeA && cube.CsnList.Count == 3, "current level slots or metadata ignored");
        Current().Items.RemoveAll(x => x.Isn == firstCube);
        Check(Get().All(x => x.Isn != firstCube), "unowned cube still appears");
    });
    await Test("invalid ownership cube class and serials cannot mutate assignments", async () => {
        Reset();
        Get();
        var before = JsonConvert.SerializeObject(Get());
        await Reject("wear", Wear(secondCube, 9999), HttpStatusCode.NotFound);
        await Reject("wear", Wear(9999, 1001), HttpStatusCode.NotFound);
        await Reject("wear", Wear(thirdCube, 1003), HttpStatusCode.BadRequest);
        await Reject("wear", Wear(secondCube, -1), HttpStatusCode.BadRequest);
        await Reject("wear", Wear(secondCube, 1003, 9999), HttpStatusCode.NotFound);
        Check(before == JsonConvert.SerializeObject(Get()), "validation partially changed mapping");
    });
    await Test("clear and malformed wear reject invalid cube and character inputs", async () => {
        Reset();
        Get();
        Current().Items.Add(new() { Isn = 2999, ItemType = 0 });
        var before = JsonConvert.SerializeObject(Get());
        await Reject("wear", Wear(2999, 1001), HttpStatusCode.BadRequest);
        await Reject("wear", new ReqWearArenaHarmonyCube { Isn = firstCube }, HttpStatusCode.BadRequest);
        await Reject("clear", new ReqClearArenaHarmonyCube { Isn = 9999 }, HttpStatusCode.NotFound);
        await Reject("clear", new ReqClearArenaHarmonyCube { Isn = firstCube, Csn = 9999 }, HttpStatusCode.NotFound);
        await Reject("clear", new ReqClearArenaHarmonyCube { Isn = firstCube, Csn = -1 }, HttpStatusCode.BadRequest);
        Check(before == JsonConvert.SerializeObject(Get()), "invalid clear or wear mutated mapping");
    });
    await Test("invalid batch is atomic across clears and transfers", async () => {
        Reset();
        Get();
        var before = JsonConvert.SerializeObject(Get());
        await Reject("management", new ReqManagementArenaHarmonyCube {
            Isn = secondCube, Clears = { 1003 }, Wears = { new NetWearHarmonyCubeData { Csn = 1001 }, new NetWearHarmonyCubeData { Csn = 9999 } }
        }, HttpStatusCode.NotFound);
        Check(before == JsonConvert.SerializeObject(Get()), "invalid batch partially moved a character");
        await Reject("management", new ReqManagementArenaHarmonyCube {
            Isn = secondCube, Wears = { new NetWearHarmonyCubeData { Csn = 1001 }, new NetWearHarmonyCubeData { Csn = 1002 }, new NetWearHarmonyCubeData { Csn = 1003 } }
        }, HttpStatusCode.BadRequest);
        Check(before == JsonConvert.SerializeObject(Get()), "overfull batch partially moved a character");
    });
    await Test("clear all stays empty after ordinary cube changes and account mappings stay separate", async () => {
        Reset();
        await Send<ResClearArenaHarmonyCube>("clear", new ReqClearArenaHarmonyCube { Isn = firstCube });
        Current().Items.Single(x => x.Isn == firstCube).CsnList = [1004];
        Check(Get().Single(x => x.Isn == firstCube).CsnList.Count == 0, "empty arena map remigrated");
        Check(Get(other).Single(x => x.Isn == firstCube).CsnList.SequenceEqual(new long[] { 1001, 1002 }), "other account arena map changed");
    });
    await Test("target zero clears a cube and successful batch swaps survive retry", async () => {
        Reset();
        await Send<ResWearArenaHarmonyCube>("wear", Wear(firstCube, 0));
        Check(Get().Single(x => x.Isn == firstCube).CsnList.Count == 0, "zero target did not clear");
        Reset();
        var batch = new ReqManagementArenaHarmonyCube { Isn = firstCube,
            Wears = { new NetWearHarmonyCubeData { Csn = 1003, SwapCsn = 1001 }, new NetWearHarmonyCubeData { Csn = 1004, SwapCsn = 1002 } } };
        await Send<ResManagementArenaHarmonyCube>("management", batch);
        await Send<ResManagementArenaHarmonyCube>("management", batch);
        Check(Get().Single(x => x.Isn == firstCube).CsnList.Order().SequenceEqual(new long[] { 1003, 1004 }), "batch swaps or retry changed final assignments");
    });
    await Test("all arena routes return explicit HTTP 401 without a session", async () => {
        Reset();
        await Reject("wear", Wear(secondCube, 1003), HttpStatusCode.Unauthorized, 0);
        await Reject("clear", new ReqClearArenaHarmonyCube { Isn = firstCube }, HttpStatusCode.Unauthorized, 0);
        await Reject("management", new ReqManagementArenaHarmonyCube { Isn = firstCube }, HttpStatusCode.Unauthorized, 0);
    });
    await Test("all arena routes reject stale user objects after a database reload", async () => {
        Reset();
        capturedUser = Current();
        Reload();
        foreach (var request in new (string Route, IMessage Request)[] {
            ("wear", Wear(secondCube, 1003)), ("clear", new ReqClearArenaHarmonyCube { Isn = firstCube }),
            ("management", new ReqManagementArenaHarmonyCube { Isn = firstCube }) })
        {
            using var response = await Raw(request.Route, request.Request, stale: true);
            Check(response.StatusCode == HttpStatusCode.Conflict, "stale user request was not rejected with 409");
        }
        Check(typeof(User).GetProperty("ArenaHarmonyCubeAssignments")!.GetValue(Current()) == null, "stale requests migrated current user");
    });
    await Test("failed save restores mutated mapping and on-disk data", async () => {
        Reset();
        Get();
        var before = JsonConvert.SerializeObject(Get());
        var saved = File.ReadAllText(jsonPath);
        using (File.Open(jsonPath + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Reject("wear", Wear(secondCube, 1001), HttpStatusCode.InternalServerError);
        Check(before == JsonConvert.SerializeObject(Get()), "save failure left mutation in memory");
        Check(saved == File.ReadAllText(jsonPath), "save failure changed persisted data");
    });
    await Test("failed clear and management saves roll back their whole operation", async () => {
        Reset();
        Get();
        var before = JsonConvert.SerializeObject(Get());
        foreach (var request in new (string Route, IMessage Request)[] {
            ("clear", new ReqClearArenaHarmonyCube { Isn = firstCube }),
            ("management", new ReqManagementArenaHarmonyCube { Isn = secondCube, Wears = { new NetWearHarmonyCubeData { Csn = 1001 } } }) })
        {
            using (File.Open(jsonPath + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                await Reject(request.Route, request.Request, HttpStatusCode.InternalServerError);
            Check(before == JsonConvert.SerializeObject(Get()), "save failure retained a partial clear/batch");
        }
    });
    await Test("failed migration save restores null so migration can retry", () => {
        Reset();
        using (File.Open(jsonPath + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            try { Get(); throw new Exception("migration did not fail"); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
        }
        Check(typeof(User).GetProperty("ArenaHarmonyCubeAssignments")!.GetValue(Current()) == null, "migration failure retained unsaved map");
        Check(Get().Single(x => x.Isn == firstCube).CsnList.Count == 2, "migration retry lost assignments");
        return Task.CompletedTask;
    });
    await Test("simultaneous target-state updates preserve single-cube assignment", async () => {
        Reset();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Send<ResWearArenaHarmonyCube>("wear", Wear(secondCube, 1001))));
        Check(Get().Sum(c => c.CsnList.Count(n => n == 1001)) == 1, "concurrent wear duplicated assignment");
        Reload();
        Check(Get().Single(x => x.Isn == secondCube).CsnList.SequenceEqual(new long[] { 1001 }), "concurrent update did not persist");
    });
    await Test("login inventory and harmony queries include independent arena cubes", async () => {
        Reset();
        await Send<ResWearArenaHarmonyCube>("wear", Wear(secondCube, 1003));
        var inventory = await Legacy<ResGetInventoryData>("inventory/get", new ReqGetInventoryData());
        var harmony = await Legacy<ResGetHarmonyCube>("inventory/getharmonycube", new ReqGetHarmonyCube());
        var lobby = await Send<ResEnterLobbyServer>("../enterlobbyserver", new ReqEnterLobbyServer());
        foreach (var values in new[] { inventory.ArenaHarmonyCubes, harmony.ArenaHarmonyCubes, lobby.ArenaHarmonyCubes })
            Check(values.Single(x => x.Isn == secondCube).CsnList.SequenceEqual(new long[] { 1003 }), "query did not include arena mapping");
        Check(harmony.HarmonyCubes.Single(x => x.Isn == firstCube).CsnList.SequenceEqual(new long[] { 1001, 1002 }), "normal query changed equipment");
    });
}
if (failures.Count != 0) { await app.StopAsync(); Console.WriteLine($"FAIL {failures.Count}/{passed + failures.Count}: {string.Join("; ", failures)}"); return 1; }
Reset();
await Send<ResWearArenaHarmonyCube>("wear", Wear(secondCube, 1003));
await app.StopAsync();
Console.WriteLine($"PASS {passed} arena cube scenarios; fixture ready for --verify-saved");
return 0;

User Fixture(ulong id) => new() {
    ID = id, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow,
    Characters = Enumerable.Range(1, 5).Select(n => new CharacterModel { Csn = 1000 + n, Tid = data.CharacterTable.Values.First().Id, Level = 1 }).ToList(),
    Items = [new() { Isn = firstCube, ItemType = cubeA, Level = 1, Count = 1, Csn = 1001, CsnList = [1001, 1002] },
        new() { Isn = secondCube, ItemType = cubeB, Level = 1, Count = 1 }, new() { Isn = thirdCube, ItemType = restrictedCube, Level = 1, Count = 1 }]
};
void Reset() { dbProperty.SetValue(null, new CoreInfo { Users = [Fixture(account), Fixture(other)] }); saveMethod.Invoke(null, null); }
void Reload() => reloadMethod.Invoke(null, null);
User Current(ulong id = account) => ((CoreInfo)dbProperty.GetValue(null)!).Users.Single(u => u.ID == id);
IReadOnlyList<NetUserHarmonyCubeData> Get(ulong id = account) => (IReadOnlyList<NetUserHarmonyCubeData>)typeof(User).Assembly
    .GetType("EpinelPS.LobbyServer.Arena.ArenaHarmonyCubeService", throwOnError: true)!.GetMethod("Get")!.Invoke(null, [Current(id)])!;
IReadOnlyList<NetUserHarmonyCubeData> Snapshot() => (IReadOnlyList<NetUserHarmonyCubeData>)typeof(User).Assembly
    .GetType("EpinelPS.LobbyServer.Arena.ArenaHarmonyCubeService", throwOnError: true)!.GetMethod("Snapshot")!.Invoke(null, [Current()])!;
ReqWearArenaHarmonyCube Wear(long isn, long csn, long swap = 0) => new() { Isn = isn, Wear = new() { Csn = csn, SwapCsn = swap } };
static void Check(bool result, string message) { if (!result) throw new Exception(message); }
async Task Test(string name, Func<Task> action) { try { await action(); passed++; Console.WriteLine($"PASS {name}"); } catch (Exception ex) { failures.Add(name); Console.WriteLine($"FAIL {name}: {ex.Message}"); } }
string PathFor(string route) => route.StartsWith("../") ? route[3..] : $"inventory/harmonycube/arena/{route}";
async Task<HttpResponseMessage> Raw(string route, IMessage request, ulong id = account, bool stale = false)
{
    using var message = new HttpRequestMessage(HttpMethod.Post, PathFor(route));
    message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream+protobuf"));
    if (id > 0) message.Headers.Add("Test-User", id.ToString());
    if (stale) message.Headers.Add("Test-Stale", "1");
    message.Content = new ByteArrayContent(request.ToByteArray());
    message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    return await client.SendAsync(message);
}
async Task<T> Send<T>(string route, IMessage request, ulong id = account) where T : IMessage<T>, new()
{
    using var response = await Raw(route, request, id);
    Check(response.IsSuccessStatusCode, $"{PathFor(route)} expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    var result = new T(); result.MergeFrom(await response.Content.ReadAsByteArrayAsync()); return result;
}
async Task Reject(string route, IMessage request, HttpStatusCode status, ulong id = account)
{
    using var response = await Raw(route, request, id);
    Check(response.StatusCode == status, $"{PathFor(route)} expected {(int)status}, got {(int)response.StatusCode}");
}
async Task<T> Legacy<T>(string route, IMessage request) where T : IMessage<T>, new()
{
    using var scope = app.Services.CreateScope();
    var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    context.Items["UserID"] = account;
    context.Request.Path = $"/v1/{route}";
    context.Request.Body = new MemoryStream(request.ToByteArray());
    context.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(context);
    var result = new T(); result.MergeFrom(((MemoryStream)context.Response.Body).ToArray()); return result;
}
sealed class CapturedUserService(User user) : IUserService { public User? GetUser() => user; }
