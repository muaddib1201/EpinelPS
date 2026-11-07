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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Paseto;
using Paseto.Builder;

const long damage = 4_720_187;
const string nonce = "000102030405060708090A0B0C0D0E0F1011121314151617";
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
if (args.Contains("--probe-live-readonly"))
{
    var live = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(root, "Server and Server selector/db.json")));
    ulong id = (ulong)live["Users"]![0]!["ID"]!;
    int tickets = (int)live["Users"]![0]!["ResetableData"]!["InterceptionTickets"]!;
    string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
        .WithKey(Convert.FromBase64String((string)live["LauncherTokenKey"]!), Encryption.SymmetricKey)
        .AddClaim("userId", id).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
    using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
    using var probe = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1/v1/intercept/") };
    probe.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    foreach (var (route, body) in new (string, IMessage)[] { ("get", new ReqGetInterceptData()), ("check", new ReqCheckClearInterceptToday { InterceptId = 1 }),
        ("fastclear", new ReqFastClearIntercept { Intercept = 9, InterceptId = 1 }) })
    {
        using var content = new ByteArrayContent(body.ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
        using var response = await probe.PostAsync(route, content);
        if (route == "fastclear") { Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid fast-clear type accepted"); Console.WriteLine("PASS live authenticated invalid fast-clear is rejected without spending attempts"); continue; }
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        if (route == "get")
        {
            var r = ResGetInterceptData.Parser.ParseFrom(bytes);
            Check(r.TicketCount == tickets && r.MaxTicketCount == (int)live["MaxInterceptionCount"]!, "live attempt counts changed");
            Console.WriteLine($"PASS live interception data: {r.TicketCount}/{r.MaxTicketCount} attempts");
        }
        else
        {
            bool expected = live["Users"]![0]!["Interception"]?["BestByBoss"]?["0:1"] is { } best &&
                (int)best["Day"]! == int.Parse(DateTime.UtcNow.AddHours(-(int)live["ResetHourUtcTime"]!).ToString("yyyyMMdd"));
            bool actual = ResCheckClearInterceptToday.Parser.ParseFrom(bytes).Clear;
            Check(actual == expected, "live daily-record lookup differs from the saved record");
            Console.WriteLine($"PASS live daily record lookup: {actual}");
        }
    }
    return 0;
}
var pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
var conn = $"Data Source={Path.Combine(AppContext.BaseDirectory, "interception.db")}";
GameContext.InitializeOptions(conn, "sqlite");
var stateProperty = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
bool restarting = args.Contains("--verify-saved");
bool liveCopy = args.Contains("--verify-live-copy");
if (liveCopy) File.WriteAllText(save, File.ReadAllText(Path.Combine(root, "Server and Server selector/db.json")));
else if (!restarting) File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo
{
    Users = Enumerable.Range(710, 24).Select(i => new User { ID = (ulong)i, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow }).ToList()
}));
var state = (CoreInfo)stateProperty.GetValue(null)!;
using (var db = GameContext.CreateNew())
{
    db.Database.EnsureCreated();
    if (!restarting)
    {
        db.Triggers.ExecuteDelete();
        db.Users.ExecuteDelete();
        db.Users.AddRange(state.Users.Select(u => new GameUser { ID = u.ID, Nickname = "P" + u.ID, LastAction = DateTime.UtcNow }));
        db.SaveChanges();
    }
}
var clock = new Clock { Now = DateTimeOffset.UtcNow };
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.AllowSynchronousIO = true; });
builder.Services.AddSingleton<TimeProvider>(clock);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(conn));
builder.Services.AddControllers(o => o.OutputFormatters.Insert(0, new ProtobufOutputFormatter())).AddApplicationPart(typeof(LobbyHandler).Assembly);
await using var app = builder.Build();
app.Use(async (ctx, next) => {
    if (ulong.TryParse(ctx.Request.Headers["Test-User"], out var id)) ctx.Items["UserID"] = id;
    if (ctx.Request.Headers.TryGetValue("Test-Nonce", out var value)) ctx.Items["Nonce"] = Convert.FromHexString(value.ToString());
    await next();
});
app.MapControllers();
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/v1/intercept/") };
int passed = 0, failed = 0;
if (liveCopy)
{
    await Test("the actual saved account can fast-clear its recovered Level D record in an isolated copy", async () => {
        var evidence = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(root, ".tools/interception-fastclear-20261106/recovery-evidence.json")));
        ulong id = (ulong)evidence["account"]!;
        var account = User(id);
        int tickets = account.ResetableData.InterceptionTickets;
        account.Interception ??= new();
        account.Interception.BestByBoss["0:1"] = new InterceptionBest
        {
            Damage = (long)evidence["damage"]!, Day = int.Parse(clock.Now.UtcDateTime.AddHours(-state.ResetHourUtcTime).ToString("yyyyMMdd"))
        };
        int before = Items(id);
        var fast = await Fast(id, nonce);
        Check(fast.Damage == damage && fast.TicketCount == tickets - 1 && fast.NormalReward.Item.Count > 0 && Items(id) > before, "real save copy did not settle");
        Console.WriteLine($"Live-save COPY only: damage={fast.Damage}, tickets={tickets}->{fast.TicketCount}, grantedItems={Items(id)-before}");
        Check(fast.Equals(await Fast(id, nonce)) && User(id).ResetableData.InterceptionTickets == tickets - 1, "real-save retry changed settlement");
    });
}
else if (!restarting)
{
    await Test("a legacy account without a paid clear cannot fast-clear or report a record", async () => {
        Check(!(await HasRecord(710)).Clear, "phantom daily record");
        var r = await Fast(710);
        Check(r.Result == FastClearResult.NoRecordToday && r.TicketCount == 3 && Items(710) == 0, "unearned fast-clear reward");
    });
    await Test("the live Level D sequence returns recorded damage, real rewards and a spent ticket", async () => {
        var manual = await Clear(711, damage, nonce);
        Check(manual.TicketCount == 2 && manual.NormalReward.Item.Count > 0 && (await HasRecord(711)).Clear, "manual clear not recorded");
        int before = Items(711);
        var fast = await Fast(711, nonce);
        Check(fast.Result == FastClearResult.Success && fast.Damage == damage && fast.TicketCount == 1 && fast.MaxTicketCount == 3, "fast-clear damage or attempts wrong");
        Check(fast.NormalReward.Item.Count > 0 && Items(711) - before == RewardCount(fast.NormalReward) + RewardCount(fast.BonusReward), "fast-clear rewards not credited");
    });
    await Test("manual and fast-clear retries return their original receipts without extra rewards", async () => {
        int items = Items(711);
        var manual = await Clear(711, damage, nonce);
        var fast = await Fast(711, nonce);
        Check(manual.TicketCount == 2 && fast.TicketCount == 1 && User(711).ResetableData.InterceptionTickets == 1 && Items(711) == items, "retry charged or rewarded twice");
    });
    await Test("another distinct fast-clear spends the final ticket and repeats at zero safely", async () => {
        var r = await Fast(711, new string('A', 48));
        Check(r.TicketCount == 0 && r.Damage == damage, "last attempt not consumed");
        int items = Items(711);
        Check(r.Equals(await Fast(711, new string('A', 48))) && Items(711) == items, "zero-ticket replay charged twice");
        using var denied = await Send(711, "fastclear", FastReq());
        Check(!denied.IsSuccessStatusCode && User(711).ResetableData.InterceptionTickets == 0 && Items(711) == items, "negative attempt or excess reward");
    });
    await Test("a lower later manual score does not replace the day's highest damage", async () => {
        await Clear(712, damage);
        await Clear(712, 1000);
        Check((await Fast(712)).Damage == damage, "lower damage replaced personal best");
    });
    await Test("record lookup is scoped to the account, boss and interception difficulty", async () => {
        Check(!(await HasRecord(713)).Clear, "another account inherited the record");
        var wrong = data.InterceptNormal.Values.First(n => n.Type == InterceptType.InterceptNormalLevel2);
        var req = new ReqFastClearIntercept { Intercept = 1, InterceptId = wrong.Id };
        Check((await Read(712, "fastclear", req, ResFastClearIntercept.Parser)).Result == FastClearResult.NoRecordToday, "another boss used the record");
    });
    await Test("Level S and special interception share complete reward and ticket settlement", async () => {
        var s = data.InterceptNormal.Values.First(n => n.Type == InterceptType.InterceptNormalLevel2);
        var special = data.InterceptSpecial.Values.First(n => n.Group == 1);
        foreach (var (id, type, boss) in new[] { (714UL, 1, s.Id), (715UL, 2, special.Id) })
        {
            await Read(id, "clear", new ReqClearIntercept { Intercept = type, InterceptId = boss, Damage = 100_000_000, BattleResult = 1 }, ResClearIntercept.Parser);
            var fast = await Read(id, "fastclear", new ReqFastClearIntercept { Intercept = type, InterceptId = boss }, ResFastClearIntercept.Parser);
            Check(fast.Damage == 100_000_000 && fast.TicketCount == 1 && fast.NormalReward.Item.Count > 0, $"type {type} settlement incomplete");
        }
    });
    await Test("failed atomic save restores rewards, tickets, records and receipts before retry", async () => {
        await Clear(716, damage);
        string before = JsonConvert.SerializeObject(User(716));
        var blocker = Directory.CreateDirectory(save + ".tmp");
        try { using var denied = await Send(716, "fastclear", FastReq(), nonce); Check(!denied.IsSuccessStatusCode, "failed save acknowledged"); }
        finally { blocker.Delete(); }
        Check(JsonConvert.SerializeObject(User(716)) == before, "failed write leaked state");
        Check((await Fast(716, nonce)).TicketCount == 1, "retry did not recover");
    });
    await Test("concurrent quick battles cannot spend the same remaining attempt", async () => {
        await Clear(717, damage);
        await Fast(717);
        var replies = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Send(717, "fastclear", FastReq())));
        try { Check(replies.Count(r => r.IsSuccessStatusCode) == 1 && User(717).ResetableData.InterceptionTickets == 0, "concurrent overspend"); }
        finally { foreach (var reply in replies) reply.Dispose(); }
    });
    await Test("invalid type/boss/damage and unauthenticated requests leave accounts unchanged", async () => {
        string before = JsonConvert.SerializeObject(User(718));
        foreach (var req in new[] { new ReqClearIntercept { Intercept = 9, InterceptId = 1, Damage = 1 },
            new ReqClearIntercept { InterceptId = 999999, Damage = 1 }, new ReqClearIntercept { InterceptId = 1, Damage = -1 },
            new ReqClearIntercept { Intercept = 1, InterceptId = 1, Damage = 1 } })
        {
            using var r = await Send(718, "clear", req);
            Check(r.StatusCode == HttpStatusCode.BadRequest, "invalid battle accepted");
        }
        foreach (string route in new[] { "get", "check", "clear", "fastclear" })
        {
            using var r = await Send(0, route, route == "clear" ? new ReqClearIntercept { InterceptId = 1 } : FastReq());
            Check(r.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated interception route");
        }
        Check(JsonConvert.SerializeObject(User(718)) == before, "invalid requests changed account");
    });
    await Test("a zero-damage paid clear is a real record with its matching tier rewards", async () => {
        await Clear(719, 0);
        Check((await HasRecord(719)).Clear, "zero damage erased a completed battle");
        var fast = await Fast(719);
        Check(fast.Result == FastClearResult.Success && fast.Damage == 0 && fast.TicketCount == 1 && fast.NormalReward != null, "zero tier settlement");
    });
    await Test("no-ticket manual clears cannot grant rewards or create a record", async () => {
        User(720).ResetableData.InterceptionTickets = 0;
        using var r = await Send(720, "clear", new ReqClearIntercept { InterceptId = 1, Damage = damage });
        Check(!r.IsSuccessStatusCode && Items(720) == 0 && !(await HasRecord(720)).Clear, "free manual clear accepted");
    });
    await Test("crossing the configured daily boundary invalidates old records but preserves retries", async () => {
        var original = await Clear(721, damage, nonce);
        var now = clock.Now;
        try
        {
            clock.Now = clock.Now.AddDays(1);
            Check(!(await HasRecord(721)).Clear && (await Fast(721)).Result == FastClearResult.NoRecordToday, "yesterday's record reused");
            int items = Items(721);
            Check(original.Equals(await Clear(721, damage, nonce)) && Items(721) == items, "cross-day replay granted a second reward");
        }
        finally { clock.Now = now; }
    });
    await Test("a restart fixture retains a daily record and completed fast-clear receipt", async () => {
        await Clear(722, damage);
        Check((await Fast(722, nonce)).TicketCount == 1, "restart fixture failed");
    });
    await Test("every distinct paid battle advances missions once even at the same clock tick", async () => {
        await Clear(723, damage);
        await Fast(723);
        await Fast(723);
        using var db = GameContext.CreateNew();
        Check(db.Triggers.Count(t => t.UserId == 723 && t.Type == Trigger.InterceptClear) == 3 &&
            db.Triggers.Count(t => t.UserId == 723 && t.Type == Trigger.InterceptStart) == 2, "distinct battles shared a mission notification");
    });
    await Test("a mission database outage retains the paid result for safe retry", async () => {
        await Clear(724, damage);
        using (var db = GameContext.CreateNew()) db.Database.ExecuteSqlRaw("ALTER TABLE Triggers RENAME TO HeldTriggers");
        try
        {
            using var failed = await Send(724, "fastclear", FastReq(), nonce);
            Check(!failed.IsSuccessStatusCode && User(724).ResetableData.InterceptionTickets == 1, "paid result not committed before notification");
        }
        finally { using var db = GameContext.CreateNew(); db.Database.ExecuteSqlRaw("ALTER TABLE HeldTriggers RENAME TO Triggers"); }
        int items = Items(724);
        Check((await Fast(724, nonce)).TicketCount == 1 && Items(724) == items, "notification retry granted a second reward");
        using var check = GameContext.CreateNew();
        Check(check.Triggers.Count(t => t.UserId == 724 && t.Type == Trigger.InterceptClear) == 2, "notification lost or duplicated after outage");
    });
}
else
{
    await Test("daily best damage and paid reward state survive process restart", async () => {
        Check((await HasRecord(722)).Clear && User(722).ResetableData.InterceptionTickets == 1 && Items(722) > 0, "restart state lost");
        int items = Items(722);
        var replay = await Fast(722, nonce);
        Check(replay.Damage == damage && replay.TicketCount == 1 && Items(722) == items, "restart retry duplicated rewards");
    });
}
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
await app.StopAsync();
return failed == 0 ? 0 : 1;

User User(ulong id) => state.Users.Single(u => u.ID == id);
int Items(ulong id) => User(id).Items.Sum(i => i.Count);
static int RewardCount(NetRewardData? r) => r?.Item.Sum(i => i.Count) ?? 0;
static ReqFastClearIntercept FastReq() => new() { InterceptId = 1 };
Task<ResClearIntercept> Clear(ulong id, long amount, string? key = null) => Read(id, "clear", new ReqClearIntercept { InterceptId = 1, Damage = amount, BattleResult = 1 }, ResClearIntercept.Parser, key);
Task<ResFastClearIntercept> Fast(ulong id, string? key = null) => Read(id, "fastclear", FastReq(), ResFastClearIntercept.Parser, key);
Task<ResCheckClearInterceptToday> HasRecord(ulong id) => Read(id, "check", new ReqCheckClearInterceptToday { InterceptId = 1 }, ResCheckClearInterceptToday.Parser);
async Task<HttpResponseMessage> Send(ulong id, string route, IMessage body, string? key = null)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, route);
    if (id != 0) req.Headers.Add("Test-User", id.ToString());
    if (key != null) req.Headers.Add("Test-Nonce", key);
    req.Content = new ByteArrayContent(body.ToByteArray());
    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    return await client.SendAsync(req);
}
async Task<T> Read<T>(ulong id, string route, IMessage body, MessageParser<T> parser, string? key = null) where T : IMessage<T>
{
    using var r = await Send(id, route, body, key);
    r.EnsureSuccessStatusCode();
    return parser.ParseFrom(await r.Content.ReadAsByteArrayAsync());
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
sealed class Clock : TimeProvider { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow() => Now; }
