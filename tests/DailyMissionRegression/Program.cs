using System.Reflection;
using System.Net.Http.Headers;
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
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
if (args.Contains("--probe-live-readonly"))
{
    var livePath = Path.Combine(root, "Server and Server selector/db.json");
    string saved = File.ReadAllText(livePath);
    var live = JObject.Parse(saved);
    var account = live["Users"]![0]!;
    string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
        .WithKey(Convert.FromBase64String((string)live["LauncherTokenKey"]!), Encryption.SymmetricKey)
        .AddClaim("userId", (ulong)account["ID"]!).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
    using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
    using var probe = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1/v1/") };
    probe.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var content = new ByteArrayContent(new ReqGetRewardedData().ToByteArray());
    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    using var response = await probe.PostAsync("mission/getrewarded/all", content);
    response.EnsureSuccessStatusCode();
    var actual = ResGetRewardedData.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
    Check(actual.DailyIds.SequenceEqual(account["ResetableData"]!["CompletedDailyMissions"]!.Values<int>()), "live daily reward claims changed");
    Check(actual.WeeklyIds.SequenceEqual(account["WeeklyResetableData"]!["CompletedWeeklyMissions"]!.Values<int>()), "live weekly reward claims changed");
    Check(actual.AchievementIds.SequenceEqual(account["CompletedAchievements"]!.Values<int>()), "live achievement claims changed");
    Check(File.ReadAllText(livePath) == saved, "read-only probe changed the save or consumed the client's refresh");
    Console.WriteLine("PASS live authenticated mission rewards match the saved account without consuming its pending history refresh");
    return 0;
}
var pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
if (args.Contains("--inspect-weekly"))
{
    Console.WriteLine(JsonConvert.SerializeObject(data.TriggerTable.Values
        .Where(r => r.HandlerType == HandlerType.MissionWeekly).OrderBy(r => r.Id)
        .Select(r => new { r.Id, Trigger = r.Trigger.ToString(), TriggerId = (int)r.Trigger,
            r.ConditionId, r.ConditionValue, r.PointValue, r.RewardId, r.DescriptionLocalkey }), Formatting.Indented));
    return 0;
}
if (args.Contains("--inspect"))
{
    foreach (var row in data.GuildLevels.Values.OrderBy(r => r.Grade))
        Console.WriteLine($"GUILD grade={row.Grade} exp={row.Exp} members={row.MaxMemberCount} activityCap={row.MaxActivityPoint}");
    foreach (var row in data.GameSettings.Where(p => p.Key.Contains("Guild") && (p.Key.Contains("Activity") || p.Key.Contains("Point"))))
        Console.WriteLine($"SETTING {row.Key}={row.Value.Value}");
    foreach (var row in data.TriggerTable.Values.Where(r => r.HandlerType == HandlerType.MissionDaily).OrderBy(r => r.Id))
        Console.WriteLine($"DAILY {row.Id}: {row.Trigger} condition={row.ConditionId}/{row.ConditionValue} points={row.PointValue} reward={row.RewardId} text={row.DescriptionLocalkey}");
    return 0;
}

var save = Path.Combine(AppContext.BaseDirectory, "db.json");
var connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "daily-mission.db")}";
GameContext.InitializeOptions(connection, "sqlite");
bool restarting = args.Contains("--verify-saved");
if (!restarting) File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo
{
    DbVersion = 5, BondRewardLevelsMigrated = true,
    Users = Enumerable.Range(810, 12).Select(i => new User
    {
        ID = (ulong)i, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow, InfraCoreLvl = 17
    }).ToList()
}));
var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var state = (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
using (var db = GameContext.CreateNew())
{
    db.Database.EnsureCreated();
    if (!restarting)
    {
    db.Triggers.ExecuteDelete();
    db.Users.ExecuteDelete();
    db.Users.AddRange(state.Users.Select(u => new GameUser { ID = u.ID, Nickname = "Test" + u.ID }));
    db.SaveChanges();
    }
}
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
int failures = 0, passes = 0;

if (restarting)
{
    await Run("a new server process retains the clock refresh and encrypted response replay", async () =>
    {
        long head;
        using (var db = GameContext.CreateNew()) head = db.Triggers.Where(t => t.UserId == 816).Max(t => t.Id);
        var request = new ReqSyncTrigger { Seq = head, IsTrustedClientSeq = true };
        var replay = await Send<ResSyncTrigger>(816, "/trigger/sync", request, nonce: new string('A', 48));
        Check(replay.Restart && replay.Triggers.Single().Seq == head, "restart lost the saved refresh response");
        var next = await Send<ResSyncTrigger>(816, "/trigger/sync", request, nonce: new string('C', 48));
        Check(!next.Restart && next.Triggers.Count == 0, "restart lost the completed clock refresh");
    });
    await Run("a new process replays completed normal and quick counseling without adding progress", async () =>
    {
        const string packet = "0102030405060708090A0B0C0D0E0F101112131415161718";
        await Counsel(819, false, packet);
        await Counsel(820, true, packet);
        AssertCounsel(819, 1);
        AssertCounsel(820, 1);
    });
    await Run("a new process delivers a saved pending counseling event once", async () =>
    {
        const string packet = "112233445566778899001122334455667788990011223344";
        await Counsel(821, true, packet);
        await Counsel(821, true, packet);
        AssertCounsel(821, 1);
    });
    Console.WriteLine($"RESULT {passes} passed, {failures} failed");
    return failures == 0 ? 0 : 1;
}

await Run("gameplay events sent to the client use UTC ticks within the action interval", () =>
{
    long before = DateTime.UtcNow.Ticks;
    var trigger = User(810).AddTrigger(Trigger.SimulationRoomStart, 1);
    long after = DateTime.UtcNow.Ticks;
    long sent = trigger.ToNet().CreatedAt;
    Check(sent >= before && sent <= after, $"event timestamp is {(sent - before) / TimeSpan.TicksPerHour} hours ahead of UTC");
    return Task.CompletedTask;
});

await Run("a retained event is sent in its original daily window without changing its stored identity", () =>
{
    var recorded = new TriggerModelNew { Id = 123, UserId = 810, Type = Trigger.InterceptStart,
        Value = 1, CreatedAt = new DateTime(2026, 11, 6, 2, 57, 0).Ticks };
    var sent = recorded.ToNet();
    Check(sent.CreatedAt == new DateTime(2026, 11, 5, 17, 57, 0, DateTimeKind.Utc).Ticks, "retained event was moved to the wrong mission day");
    Check(recorded.CreatedAt == new DateTime(2026, 11, 6, 2, 57, 0).Ticks && sent.Seq == 123 && sent.Trigger == 81 && sent.UserValue == 1,
        "normalization changed the stored timestamp, sequence, or event payload");
    return Task.CompletedTask;
});

await Run("normal counseling of a new and existing bond advances the daily task once per action", async () =>
{
    await Counsel(811, quick: false);
    await Counsel(811, quick: false);
    AssertCounsel(811, 2);
});

await Run("quick counseling of a new and existing bond advances the daily task once per action", async () =>
{
    await Counsel(812, quick: true);
    await Counsel(812, quick: true);
    AssertCounsel(812, 2);
});

await Run("trigger sync includes counseling progress for only the current account", async () =>
{
    var sync = await Send<ResSyncTrigger>(811, "/trigger/sync", new ReqSyncTrigger());
    Check(sync.Triggers.Count(t => t.Trigger == 44 && t.ConditionId == 0 && t.UserValue == 1) == 2, "counseling events are missing from the client response");
    Check(sync.Triggers.Count == 2 && !sync.HasRemainData, "another account's events leaked into sync");
});

await Run("a cached sequence restarts once so the client can replace the incorrectly dated history", async () =>
{
    var retained = User(813).AddTrigger(Trigger.SimulationRoomStart, 1);
    var sync = await Send<ResSyncTrigger>(813, "/trigger/sync", new ReqSyncTrigger { Seq = retained.Id, IsTrustedClientSeq = true });
    Check(sync.Restart && sync.Triggers.Count == 1 && sync.Triggers[0].Seq == retained.Id, "cached client was not given a corrected full history");
    Check(sync.Triggers[0].CreatedAt <= DateTime.UtcNow.Ticks, "full history still contains a future timestamp");
    var next = await Send<ResSyncTrigger>(813, "/trigger/sync", new ReqSyncTrigger { Seq = retained.Id, IsTrustedClientSeq = true });
    Check(!next.Restart && next.Triggers.Count == 0, "full history restarted repeatedly");
    jsonDb.GetMethod("Reload")!.Invoke(null, null);
    var reloaded = await Send<ResSyncTrigger>(813, "/trigger/sync", new ReqSyncTrigger { Seq = retained.Id, IsTrustedClientSeq = true });
    Check(!reloaded.Restart && reloaded.Triggers.Count == 0, "server reload lost the completed clock refresh");
});

await Run("a corrected full history remains pageable without restarting its second page", async () =>
{
    using (var db = GameContext.CreateNew())
    {
        db.Triggers.AddRange(Enumerable.Range(0, 2001).Select(i => new TriggerModelNew
        {
            UserId = 814, Type = Trigger.CampaignStart, Value = 1, CreatedAt = new DateTime(2026, 11, 6, 2, 57, 0).Ticks
        }));
        db.SaveChanges();
    }
    long head;
    using (var db = GameContext.CreateNew()) head = db.Triggers.Where(t => t.UserId == 814).Max(t => t.Id);
    var first = await Send<ResSyncTrigger>(814, "/trigger/sync", new ReqSyncTrigger { Seq = head, IsTrustedClientSeq = true });
    Check(first.Restart && first.Triggers.Count == 2000 && first.HasRemainData, "clock refresh did not return the first history page");
    var second = await Send<ResSyncTrigger>(814, "/trigger/sync", new ReqSyncTrigger { Seq = first.Triggers[^1].Seq });
    Check(!second.Restart && second.Triggers.Count == 1 && !second.HasRemainData && second.Triggers[0].Seq == head, "second history page was lost or restarted");
});

await Run("a failed history response leaves the clock refresh retryable", async () =>
{
    var retained = User(815).AddTrigger(Trigger.SimulationRoomStart, 1);
    var request = new ReqSyncTrigger { Seq = retained.Id, IsTrustedClientSeq = true };
    try
    {
        await Send<ResSyncTrigger>(815, "/trigger/sync", request, new BrokenWriteStream());
        throw new InvalidOperationException("broken transport reported success");
    }
    catch (IOException) { }
    jsonDb.GetMethod("Reload")!.Invoke(null, null);
    var retry = await Send<ResSyncTrigger>(815, "/trigger/sync", request);
    Check(retry.Restart && retry.Triggers.Count == 1, "failed response permanently consumed the history refresh");
});

await Run("a retransmitted encrypted history request replays its Restart response after reload", async () =>
{
    var retained = User(816).AddTrigger(Trigger.SimulationRoomStart, 1);
    var request = new ReqSyncTrigger { Seq = retained.Id, IsTrustedClientSeq = true };
    var first = await Send<ResSyncTrigger>(816, "/trigger/sync", request, nonce: new string('A', 48));
    Check(first.Restart, "fixture did not refresh cached history");
    jsonDb.GetMethod("Reload")!.Invoke(null, null);
    var retry = await Send<ResSyncTrigger>(816, "/trigger/sync", request, nonce: new string('A', 48));
    Check(first.Equals(retry), "transport retransmission lost the Restart response");
    var next = await Send<ResSyncTrigger>(816, "/trigger/sync", request, nonce: new string('B', 48));
    Check(!next.Restart && next.Triggers.Count == 0, "new packet replayed the old refresh");
});

foreach (bool quick in new[] { false, true })
{
    ulong id = quick ? 818UL : 817UL;
    await Run($"{(quick ? "quick" : "normal")} counseling rolls back when its JSON save fails", async () =>
    {
        string before = JsonConvert.SerializeObject(User(id));
        string saved = File.ReadAllText(save);
        Directory.CreateDirectory(save + ".tmp");
        try
        {
            try { await Counsel(id, quick); throw new InvalidOperationException("failed save reported success"); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        finally { Directory.Delete(save + ".tmp"); }
        Check(JsonConvert.SerializeObject(User(id)) == before && File.ReadAllText(save) == saved, "failed save left counseling state in memory or on disk");
        using var db = GameContext.CreateNew();
        Check(!db.Triggers.Any(t => t.UserId == id && t.Type == Trigger.CharacterCounsel), "failed save committed an orphaned mission event");
    });

    ulong outageId = quick ? 820UL : 819UL;
    await Run($"{(quick ? "quick" : "normal")} counseling retries a saved event after a mission database outage without repeating the action", async () =>
    {
        const string packet = "0102030405060708090A0B0C0D0E0F101112131415161718";
        using (var db = GameContext.CreateNew())
            db.Database.ExecuteSqlRaw("CREATE TRIGGER fail_counsel BEFORE INSERT ON Triggers WHEN NEW.Type=44 BEGIN SELECT RAISE(ABORT,'test interruption'); END;");
        try
        {
            try { await Counsel(outageId, quick, packet); throw new InvalidOperationException("failed mission write reported success"); }
            catch (DbUpdateException) { }
        }
        finally { using var db = GameContext.CreateNew(); db.Database.ExecuteSqlRaw("DROP TRIGGER fail_counsel"); }
        jsonDb.GetMethod("Reload")!.Invoke(null, null);
        Check(User(outageId).BondInfo.Single().CounseledCount == 1, "committed counseling was lost during notification outage");
        await Counsel(outageId, quick, packet);
        await Counsel(outageId, quick, packet);
        AssertCounsel(outageId, 1);
    });
}

await Run("a stale request cannot deliver or replay counseling rolled back by a failed save", async () =>
{
    var stale = User(817);
    const string packet = "AABBCCDDEEFF001122334455667788990011223344556677";
    Directory.CreateDirectory(save + ".tmp");
    try
    {
        try { await Counsel(817, true, packet); throw new InvalidOperationException("failed save reported success"); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
    finally { Directory.Delete(save + ".tmp"); }
    Check(!ReferenceEquals(stale, User(817)), "failed save did not restore the account snapshot");
    var helper = typeof(User).Assembly.GetType("EpinelPS.LobbyServer.Character.Counsel.CounselMissionDelivery")!
        .GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(ResCharacterQuickCounsel));
    Exception? rejected = null;
    lock (stale)
    lock (jsonDb.GetField("SaveLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
    {
        try
        {
            helper.Invoke(null, new object[] { stale, new ReqCharacterQuickCounsel { NameCode = 5185 }, Convert.FromHexString(packet),
                new Func<ResCharacterQuickCounsel>(() => throw new InvalidOperationException("stale action was applied")) });
        }
        catch (TargetInvocationException e) { rejected = e.InnerException; }
    }
    Check(rejected is UnauthorizedAccessException, "stale account replayed the failed action instead of rejecting it");
    using var db = GameContext.CreateNew();
    Check(!db.Triggers.Any(t => t.UserId == 817 && t.Type == Trigger.CharacterCounsel), "stale request delivered an uncommitted mission event");
});

await Run("a committed pending counseling event remains recoverable for a fresh process", async () =>
{
    using (var db = GameContext.CreateNew())
        db.Database.ExecuteSqlRaw("CREATE TRIGGER fail_counsel BEFORE INSERT ON Triggers WHEN NEW.Type=44 BEGIN SELECT RAISE(ABORT,'test interruption'); END;");
    try
    {
        try { await Counsel(821, true, "112233445566778899001122334455667788990011223344"); throw new InvalidOperationException("failed mission write reported success"); }
        catch (DbUpdateException) { }
    }
    finally { using var db = GameContext.CreateNew(); db.Database.ExecuteSqlRaw("DROP TRIGGER fail_counsel"); }
    var saved = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))!.Users.Single(u => u.ID == 821);
    using var check = GameContext.CreateNew();
    Check(saved.BondInfo.Single().CounseledCount == 1 && saved.CounselMissionReceipts.Single().PendingTrigger &&
        !check.Triggers.Any(t => t.UserId == 821 && t.Type == Trigger.CharacterCounsel), "restart fixture lost the pending counseling notification");
});

Console.WriteLine($"RESULT {passes} passed, {failures} failed");
return failures == 0 ? 0 : 1;

User User(ulong id) => ((CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!).Users.Single(u => u.ID == id);

async Task Counsel(ulong id, bool quick, string? nonce = null)
{
    if (quick) await Send<ResCharacterQuickCounsel>(id, "/character/counsel/quick", new ReqCharacterQuickCounsel { NameCode = 5185 }, nonce: nonce);
    else await Send<ResCharacterCounsel>(id, "/character/attractive/counsel", new ReqCharacterCounsel { NameCode = 5185, CounselTid = 3387 }, nonce: nonce);
}

void AssertCounsel(ulong id, int count)
{
    using var db = GameContext.CreateNew();
    var events = db.Triggers.Where(t => t.UserId == id && t.Type == Trigger.CharacterCounsel).ToArray();
    Check(events.Length == count && events.All(t => t.Value == 1 && t.ConditionId == 0), $"expected {count} counseling events, received {events.Length}");
    Check(events.All(t => t.ToNet().CreatedAt <= DateTime.UtcNow.Ticks && t.ToNet().CreatedAt >= DateTime.UtcNow.AddMinutes(-5).Ticks), "counseling event is outside the current mission time window");
    Check(User(id).BondInfo.Single().CounseledCount == count, "bond counseling count changed incorrectly");
}

async Task<T> Send<T>(ulong id, string route, IMessage body, Stream? transport = null, string? nonce = null) where T : IMessage<T>, new()
{
    using var scope = services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Items["UserID"] = id;
    if (nonce != null) ctx.Items["Nonce"] = Convert.FromHexString(nonce);
    ctx.Request.Path = "/v1" + route;
    ctx.Request.Body = new MemoryStream(body.ToByteArray());
    ctx.Response.Body = transport ?? new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    var response = new T();
    response.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray());
    return response;
}

async Task Run(string name, Func<Task> test)
{
    try { await test(); passes++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}

static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

sealed class BrokenWriteStream : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count) => throw new IOException("test transport failure");
    public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("test transport failure");
}
