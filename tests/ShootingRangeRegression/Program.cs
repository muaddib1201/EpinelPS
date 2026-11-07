using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using ASodium;
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

const ulong first = 801, second = 802, foreign = 803, outsider = 804;
const long initialScore = 5_000_000_000, bestScore = 8_000_000_000;
const string nonce = "000102030405060708090A0B0C0D0E0F1011121314151617";
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
if (args.Contains("--probe-live-readonly"))
{
    var live = JObject.Parse(File.ReadAllText(Path.Combine(workspace, "Server and Server selector/db.json")));
    string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
        .WithKey(Convert.FromBase64String((string)live["LauncherTokenKey"]!), Encryption.SymmetricKey)
        .AddClaim("userId", (ulong)live["Users"]![0]!["ID"]!).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
    using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
    using var probe = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1/v1/") };
    probe.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    foreach (var route in new[] { "shootingrange/v2/get", "shootingrange/getbattlelog" })
    {
        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
        using var response = await probe.PostAsync(route, content);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        if (route.EndsWith("get"))
        {
            var rankings = ResGetShootingRangeRankingV2.Parser.ParseFrom(bytes);
            Check(rankings.RankingList.Count == 15 && rankings.Result == ShootingRangeRankingResult.GetShootingRangeRankingResultSuccess, "live boards incomplete");
            Console.WriteLine($"PASS live authenticated ranking: {rankings.RankingList.Count} boards");
        }
        else Console.WriteLine($"PASS live authenticated history: {ResGetShootingRangeBattleLog.Parser.ParseFrom(bytes).BattleLogList.Count} battles");
    }
    using var invalid = new ByteArrayContent(new ReqSetShootingRangeScoreV2 { ShootingRangeId = -1, Score = 1 }.ToByteArray());
    invalid.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    using var rejected = await probe.PostAsync("shootingrange/v2/set", invalid);
    Check(rejected.StatusCode == HttpStatusCode.BadRequest, "live invalid submission not rejected");
    Console.WriteLine("PASS live authenticated submission route rejects invalid stages without writing a battle");
    return 0;
}
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
var conn = $"Data Source={Path.Combine(AppContext.BaseDirectory, "range-regression.db")}";
GameContext.InitializeOptions(conn, "sqlite");
bool restarting = args.Contains("--verify-saved");
if (!restarting)
{
    var users = new[] { Account(first, 1), Account(second, 1), Account(foreign, 2), Account(outsider, null) };
    var state = JObject.FromObject(new CoreInfo { Users = [.. users], Guilds = new()
    {
        [1] = new SavedGuild { Data = new NetGuildData { Gsn = 1, Name = "First", Grade = 1 } },
        [2] = new SavedGuild { Data = new NetGuildData { Gsn = 2, Name = "Other", Grade = 1 } }
    } });
    // A real legacy save has none of the new record fields.
    foreach (var user in state["Users"]!) user["ShootingRangeBattles"]?.Parent?.Remove();
    foreach (var guild in ((JObject)state["Guilds"]!).Properties()) ((JObject)guild.Value).Remove("ShootingRangeScores");
    File.WriteAllText(save, state.ToString());
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
    db.Users.AddRange(users.Select(u => new GameUser { ID = u.ID, Nickname = $"P{u.ID}", LastAction = DateTime.UtcNow }));
    db.SaveChanges();
}
var builder = WebApplication.CreateBuilder();
string? internalAddress = null;
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.AllowSynchronousIO = true; });
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(conn));
builder.Services.AddHttpClient("Internal").ConfigurePrimaryHttpMessageHandler(() => new RebasedHandler(() => internalAddress!));
builder.Services.AddControllers(o => o.OutputFormatters.Insert(0, new ProtobufOutputFormatter()))
    .AddApplicationPart(typeof(LobbyHandler).Assembly);
await using var app = builder.Build();
app.UseMiddleware<EncryptionMiddleware>();
app.Use(async (ctx, next) => {
    if (ulong.TryParse(ctx.Request.Headers["Test-User"], out var id)) ctx.Items["UserID"] = id;
    if (ctx.Request.Headers.TryGetValue("Test-Nonce", out var value)) ctx.Items["Nonce"] = Convert.FromHexString(value.ToString());
    await next();
});
app.MapControllers();
await app.StartAsync();
internalAddress = app.Urls.Single();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/v1/") };
var failures = new List<string>();
int passed = 0;
if (!restarting)
{
    await Test("legacy saves return fifteen complete empty boards and empty personal history", async () => {
        var rankings = await Rankings(first);
        Check(rankings.RankingList.Count == 15 && rankings.RankingList.All(b => b.GuildRankingList.Count == 0 &&
            b.UserGuildRanking is { Rank: 0, Score: 0, User.Usn: (long)first, User.UserTitleDisplayData: not null }), "invalid empty board");
        Check((await History(first)).BattleLogList.Count == 0, "phantom history");
    });
    await Test("submission saves a 64-bit best score and returns its ranking", async () => {
        var r = await Submit(first, Request(1, initialScore), nonce);
        Check(r.IsNewRecord && r.PrevRanking == 0 && r.NextRanking == 1 && r.HighestScore == initialScore, "new record reply");
        var board = await Board(first, 1);
        Check(board.UserGuildRanking.Score == initialScore && board.UserGuildRanking.Rank == 1 &&
            board.GuildRankingList.Single().User.Nickname == "P801", "submitted score absent from board");
    });
    await Test("history snapshots keep battle result, UTC time, appearance, statistics and squad", async () => {
        var row = (await History(first)).BattleLogList.Single();
        Check(row.Score == initialScore && row.ShootingRangeId == 1 && row.BattleResult == 2 &&
            row.CreatedAt.ToDateTime() > DateTime.UtcNow.AddMinutes(-3), "battle fields missing");
        Check(row.UserTeam.TeamNumber == 1 && row.UserTeam.Slots.Count == 5 && row.CharacterDataList.Count == 5, "missing squad");
        var member = row.CharacterDataList[0];
        Check(member.Slot == 1 && member.Lv == 237 && member.Grade == 3 && member.Core == 7 &&
            member.CostumeId == 10016 && member.Combat == 123456 && member.NameCode > 0, "character snapshot incomplete");
        MemoryUser(first).Characters[0].CostumeId = 10017;
        Check((await History(first)).BattleLogList[0].CharacterDataList[0].CostumeId == 10016, "history changed with character appearance");
    });
    await Test("same transport retry returns the original receipt without duplicate history", async () => {
        var replay = await Submit(first, Request(1, initialScore), nonce);
        Check(replay.IsNewRecord && replay.PrevRanking == 0 && replay.NextRanking == 1 &&
            (await History(first)).BattleLogList.Count == 1, "retry changed the receipt or duplicated history");
    });
    await Test("lower and equal scores keep the best while retaining distinct new battles", async () => {
        foreach (long score in new[] { initialScore - 1, initialScore })
        {
            var r = await Submit(first, Request(1, score));
            Check(!r.IsNewRecord && r.HighestScore == initialScore && r.PrevRanking == 1 && r.NextRanking == 1, "lower/equal score changed record");
        }
        Check((await History(first)).BattleLogList.Count == 3, "distinct battles were collapsed");
    });
    await Test("guild rankings reorder on improvements and break equal scores consistently", async () => {
        await Submit(second, Request(1, 6_000_000_000));
        Check((await Board(first, 1)).UserGuildRanking.Rank == 2, "member ranking did not move");
        var improved = await Submit(first, Request(1, bestScore));
        Check(improved.IsNewRecord && improved.PrevRanking == 2 && improved.NextRanking == 1 && improved.HighestScore == bestScore, "improvement ranking reply");
        await Submit(second, Request(1, bestScore));
        Check((await Board(second, 1)).GuildRankingList.Select(r => r.User.Usn).SequenceEqual(new long[] { (long)first, (long)second }), "unstable tied ranks");
    });
    await Test("another guild and private battle history are isolated", async () => {
        await Submit(foreign, Request(1, long.MaxValue - 1));
        var own = await Board(first, 1);
        Check(own.GuildRankingList.Count == 2 && own.GuildRankingList.All(r => r.User.Usn != (long)foreign), "foreign guild score leaked");
        Check((await Board(foreign, 1)).GuildRankingList.Single().Score == long.MaxValue - 1, "64-bit score truncated");
        Check((await History(second)).BattleLogList.All(r => r.Score != initialScore - 1), "another player's history leaked");
    });
    await Test("all regular element and duration categories save independently", async () => {
        foreach (var stage in data.ShootingRanges.Values.Where(s => !s.SpecActiveSpot))
        {
            if (stage.Id == 1) continue;
            long score = 100_000 + stage.Id;
            await Submit(foreign, Request(stage.Id, score));
            Check((await Board(foreign, stage.Id)).UserGuildRanking.Score == score, $"wrong category {stage.Id}");
        }
        Check((await Board(foreign, 1)).UserGuildRanking.Score == long.MaxValue - 1, "other category overwrote Fire/Short");
    });
    await Test("fixed-spec practice retains history without replacing the regular board", async () => {
        var r = await Submit(first, Request(2, 900_000_000_000));
        Check(!r.IsNewRecord && r.HighestScore == bestScore && (await Board(first, 1)).UserGuildRanking.Score == bestScore, "fixed-spec practice entered regular rankings");
        Check((await History(first)).BattleLogList[0].ShootingRangeId == 2, "fixed-spec battle lost");
    });
    await Test("missing telemetry snapshots the selected element preset, with no representative-team substitution", async () => {
        await Submit(first, new ReqSetShootingRangeScoreV2 { ShootingRangeId = 17, Score = 123, BattleResult = 1 });
        var row = (await History(first)).BattleLogList[0];
        Check(row.UserTeam.TeamNumber == 2 && row.UserTeam.Slots.Single().Value == 5 &&
            row.CharacterDataList.Single().Slot == 3 && row.CharacterDataList[0].Tid == MemoryUser(first).Characters[4].Tid, "wrong saved preset");
    });
    await Test("submitted roster wins over changed presets and accepts zero-based combat slots", async () => {
        var req = new ReqSetShootingRangeScoreV2 { ShootingRangeId = 3, Score = 100, AntiCheatBattleData = new() };
        req.AntiCheatBattleData.Characters.Add(new NetAntiCheatCharacter { Csn = 4, Tid = MemoryUser(first).Characters[3].Tid, Slot = 0,
            CharacterSpec = new NetAntiCheatCharacterSpec { Level = 250, Grade = 3, Core = 1, Combat = 777 } });
        await Submit(first, req);
        var row = (await History(first)).BattleLogList[0];
        Check(row.UserTeam.Slots.Single().Value == 4 && row.CharacterDataList.Single().Lv == 250 &&
            row.CharacterDataList[0].Slot == 1 && row.CharacterDataList[0].Combat == 777, "submitted battle roster not captured");
    });
    await Test("zero-score battles are recorded without a phantom best or rank", async () => {
        var r = await Submit(first, Request(19, 0));
        Check(!r.IsNewRecord && r.HighestScore == 0 && r.NextRanking == 0 &&
            (await Board(first, 19)).UserGuildRanking.Rank == 0, "zero-score rank");
        Check((await History(first)).BattleLogList[0].Score == 0, "zero-score history missing");
    });
    await Test("invalid sessions, stages, negative scores and nonmembers cannot write records", async () => {
        int before = (await History(first)).BattleLogList.Count;
        foreach (var req in new[] { Request(-1, 5), Request(999999, 5), Request(1, -1) })
        {
            using var r = await Send(first, "shootingrange/v2/set", req);
            Check(r.StatusCode == HttpStatusCode.BadRequest, "invalid submission accepted");
        }
        foreach (string route in new[] { "shootingrange/v2/get", "shootingrange/v2/set", "shootingrange/getbattlelog" })
        {
            using var r = await Send(0, route, route.EndsWith("set") ? Request(1, 3) : new ReqGetShootingRangeRankingV2());
            Check(r.StatusCode == HttpStatusCode.Unauthorized, $"unauthenticated route {route}");
        }
        Check((await Submit(outsider, Request(1, 100))).Result == ShootingRangeRankingResult.GetShootingRangeRankingResultNotMember, "nonmember submission");
        using var history = await Send(outsider, "shootingrange/getbattlelog", new ReqGetShootingRangeBattleLog());
        Check(history.StatusCode == HttpStatusCode.Forbidden && (await History(first)).BattleLogList.Count == before, "unauthorized history or invalid mutation");
    });
    await Test("failed persistence rolls back the board and personal history before retry", async () => {
        var before = (await History(first)).ToByteArray();
        var blocker = Directory.CreateDirectory(save + ".tmp");
        try
        {
            using var r = await Send(first, "shootingrange/v2/set", Request(1, bestScore + 1));
            Check(!r.IsSuccessStatusCode, "failed save acknowledged");
        }
        finally { blocker.Delete(); }
        var after = (await History(first)).ToByteArray();
        Check((await Board(first, 1)).UserGuildRanking.Score == bestScore &&
            before.SequenceEqual(after), "failed save leaked into memory");
    });
    await Test("concurrent submissions keep the largest score and every distinct battle", async () => {
        int count = (await History(first)).BattleLogList.Count;
        await Task.WhenAll(Enumerable.Range(1, 12).Select(i => Submit(first, Request(5, 10_000 + i))));
        Check((await Board(first, 5)).UserGuildRanking.Score == 10_012 &&
            (await History(first)).BattleLogList.Count == count + 12, "concurrent submissions lost records");
    });
    await Test("membership changes remove former guild scores from the visible board", async () => {
        var account = MemoryUser(second);
        account.Guild.guildId = 2;
        Check((await Board(first, 1)).GuildRankingList.All(r => r.User.Usn != (long)second) &&
            (await Board(second, 1)).UserGuildRanking.Score == 0, "past guild score leaked into the new guild");
        account.Guild.guildId = 1;
    });
    await Test("a batch retry preserves its original receipt and adds only one personal battle", async () => {
        int count = (await History(first)).BattleLogList.Count;
        var req = Request(11, 2_123_000_000);
        var original = await BatchSubmit(first, req);
        var replay = await BatchSubmit(first, req);
        Check(original.IsNewRecord && original.Equals(replay) && (await History(first)).BattleLogList.Count == count + 1,
            "batch retry lost its identity or duplicated history");
    });
    await Test("a retained packet replay after a guild change never credits the new guild", async () => {
        var req = Request(15, 1_234_567_890);
        var original = await Submit(first, req, nonce);
        int count = (await History(first)).BattleLogList.Count;
        var account = MemoryUser(first);
        try
        {
            account.Guild.guildId = 2;
            var replay = await Submit(first, req, nonce);
            Check(original.Equals(replay) && (await History(first)).BattleLogList.Count == count &&
                (await Board(first, 15)).UserGuildRanking.Score == 0, "old battle credited to the new guild");
            account.Guild.guildId = null;
            Check(original.Equals(await Submit(first, req, nonce)), "leaving a guild changed a retained receipt");
        }
        finally { account.Guild.guildId = 1; }
    });
    await Test("reported support characters retain their identifier and battle statistics", async () => {
        int tid = MemoryUser(first).Characters[1].Tid;
        var req = new ReqSetShootingRangeScoreV2 { ShootingRangeId = 7, Score = 100, AntiCheatBattleData = new() };
        req.AntiCheatBattleData.Characters.Add(new NetAntiCheatCharacter { Tid = tid, Slot = 1, SupportCharacterId = 123,
            CharacterSpec = new NetAntiCheatCharacterSpec { Level = 200, Grade = 3, Core = 2, Combat = 333 } });
        await Submit(first, req);
        var row = (await History(first)).BattleLogList[0];
        Check(row.UserTeam.Slots.Single() is { Value: 123, ValueType: 1 } &&
            row.CharacterDataList.Single() is { Lv: 200, Core: 2, Combat: 333 } c && c.Tid == tid,
            "support identifier or statistics lost");
    });
    await Test("history is bounded independently of persistent best scores", async () => {
        for (int i = 0; i < 101; i++) await Submit(first, Request(9, 30_000 + i));
        Check((await History(first)).BattleLogList.Count == 100 &&
            (await Board(first, 1)).UserGuildRanking.Score == bestScore, "history pruning erased a best score or exceeded limit");
        await Submit(first, Request(9, 30_101), nonce);
    });
}
else
{
    await Test("guild best scores and personal snapshots survive a fresh process", async () => {
        Check((await Board(first, 1)).UserGuildRanking.Score == bestScore &&
            (await Board(first, 9)).UserGuildRanking.Score == 30_101, "best scores lost after restart");
        var history = await History(first);
        Check(history.BattleLogList.Count == 100 && history.BattleLogList[0].Score == 30_101 &&
            history.BattleLogList[0].CharacterDataList.Count == 5 && history.BattleLogList[0].CreatedAt != null, "history lost after restart");
    });
    await Test("retained submission receipt replays after restart without duplication", async () => {
        var r = await Submit(first, Request(9, 30_101), nonce);
        Check(r.IsNewRecord && r.HighestScore == 30_101 && (await History(first)).BattleLogList.Count == 100, "retry receipt or history lost after restart");
    });
}
Console.WriteLine($"RESULT {passed} passed, {failures.Count} failed");
await app.StopAsync();
return failures.Count == 0 ? 0 : 1;

User Account(ulong id, int? guild)
{
    var user = new User { ID = id, Guild = new GuildData { guildId = guild } };
    user.userPointData.UserLevel = 20;
    user.Characters = data.CharacterTable.Values.Where(c => c.NameCode > 0).Take(5).Select((c, i) =>
        new CharacterModel { Csn = i + 1, Tid = c.Id, Level = 237, Grade = 10, CostumeId = 10016 }).ToList();
    var team = new NetTeamData { TeamNumber = 1 };
    for (int i = 1; i <= 5; i++) team.Slots.Add(new NetTeamSlot { Slot = i, Value = i });
    user.UserTeams[24] = new NetUserTeamData { Type = 24, LastContentsTeamNumber = 1, Teams = { team } };
    user.UserTeams[28] = new NetUserTeamData { Type = 28, LastContentsTeamNumber = 2,
        Teams = { new NetTeamData { TeamNumber = 1, Slots = { new NetTeamSlot { Slot = 1, Value = 1 } } },
                  new NetTeamData { TeamNumber = 2, Slots = { new NetTeamSlot { Slot = 3, Value = 5 } } } } };
    return user;
}
User MemoryUser(ulong id) => (User)typeof(GameData).Assembly.GetType("EpinelPS.Database.JsonDb")!
    .GetMethod("GetUser")!.Invoke(null, [id])!;
ReqSetShootingRangeScoreV2 Request(int stage, long score)
{
    var req = new ReqSetShootingRangeScoreV2 { ShootingRangeId = stage, Score = score, BattleResult = 2, AntiCheatBattleData = new() };
    var characters = restarting ? MemoryUser(first).Characters : data.CharacterTable.Values.Where(c => c.NameCode > 0).Take(5)
        .Select((c, i) => new CharacterModel { Csn = i + 1, Tid = c.Id }).ToList();
    foreach (var c in characters) req.AntiCheatBattleData.Characters.Add(new NetAntiCheatCharacter { Csn = c.Csn, Tid = c.Tid, Slot = c.Csn,
        CharacterSpec = new NetAntiCheatCharacterSpec { Level = 237, Grade = 3, Core = 7, Combat = 123456 } });
    return req;
}
Task<ResSetShootingRangeScoreV2> Submit(ulong id, ReqSetShootingRangeScoreV2 req, string? key = null) =>
    Read(id, "shootingrange/v2/set", req, ResSetShootingRangeScoreV2.Parser, key);
Task<ResGetShootingRangeRankingV2> Rankings(ulong id) => Read(id, "shootingrange/v2/get", new ReqGetShootingRangeRankingV2(), ResGetShootingRangeRankingV2.Parser);
async Task<NetShootingRangeRankingTotalData> Board(ulong id, int stage) => (await Rankings(id)).RankingList.Single(b => b.ShootingRangeId == stage);
Task<ResGetShootingRangeBattleLog> History(ulong id) => Read(id, "shootingrange/getbattlelog", new ReqGetShootingRangeBattleLog(), ResGetShootingRangeBattleLog.Parser);
async Task<ResSetShootingRangeScoreV2> BatchSubmit(ulong id, ReqSetShootingRangeScoreV2 req)
{
    var state = (CoreInfo)typeof(GameData).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!.GetValue(null)!;
    string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local).WithKey(state.LauncherTokenKey, Encryption.SymmetricKey)
        .AddClaim("userId", id).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
    var headers = Encoding.ASCII.GetBytes("POST /v1/shootingrange/v2/set HTTP/1.1\r\nAuthorization: Bearer " + token + "\r\n\r\n");
    using var content = new MultipartContent("mixed", "test-range-boundary");
    var part = new ByteArrayContent([.. headers, .. req.ToByteArray()]);
    part.Headers.ContentType = new MediaTypeHeaderValue("application/http");
    part.Headers.Add("Content-ID", "score");
    content.Add(part);
    byte[] plain = await content.ReadAsByteArrayAsync();
    var info = new GameClientInfo { UserId = id, Keys = new SodiumKeyExchangeSharedSecretBox
        { ReadSharedSecret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), TransferSharedSecret = new byte[32] } };
    string encryptionToken = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local).WithKey(state.LauncherTokenKey, Encryption.SymmetricKey)
        .AddClaim("data", System.Text.Json.JsonSerializer.Serialize(info)).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
    byte[] tokenBytes = Encoding.UTF8.GetBytes(encryptionToken);
    byte[] tokenLength = [(byte)(tokenBytes.Length >> 8), (byte)tokenBytes.Length];
    byte[] packetNonce = Convert.FromHexString(nonce);
    byte[] additionalData = [130, 1, 89, .. tokenLength, .. tokenBytes];
    byte[] encrypted = SodiumSecretAeadXChaCha20Poly1305IETF.Encrypt([67, 129, 0, .. plain], packetNonce, info.Keys.ReadSharedSecret, additionalData);
    int headerLength = 2 + 3 + tokenBytes.Length + 2 + packetNonce.Length;
    var packet = new ByteArrayContent([89, (byte)(headerLength >> 8), (byte)headerLength, 131, 1, 89, .. tokenLength, .. tokenBytes, 88, 24, .. packetNonce, .. encrypted]);
    packet.Headers.ContentType = content.Headers.ContentType;
    packet.Headers.TryAddWithoutValidation("Content-Encoding", "gzip,enc");
    using var message = new HttpRequestMessage(HttpMethod.Post, "$batch") { Content = packet };
    message.RequestUri = new Uri(internalAddress + "/$batch");
    using var r = await client.SendAsync(message);
    r.EnsureSuccessStatusCode();
    var parts = await r.Content.ReadAsMultipartAsync();
    byte[] body = await parts.Contents.Single().ReadAsByteArrayAsync();
    Check(Encoding.ASCII.GetString(body).StartsWith("HTTP/1.1 200"), "batch part failed");
    int offset = Encoding.Latin1.GetString(body).IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
    return ResSetShootingRangeScoreV2.Parser.ParseFrom(body[offset..]);
}
async Task<HttpResponseMessage> Send(ulong id, string route, IMessage req, string? key = null)
{
    using var message = new HttpRequestMessage(HttpMethod.Post, route);
    if (id != 0) message.Headers.Add("Test-User", id.ToString());
    if (key != null) message.Headers.Add("Test-Nonce", key);
    message.Content = new ByteArrayContent(req.ToByteArray());
    message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    return await client.SendAsync(message);
}
async Task<T> Read<T>(ulong id, string route, IMessage req, MessageParser<T> parser, string? key = null) where T : IMessage<T>
{
    using var r = await Send(id, route, req, key);
    r.EnsureSuccessStatusCode();
    return parser.ParseFrom(await r.Content.ReadAsByteArrayAsync());
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures.Add(name); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }

sealed class RebasedHandler(Func<string> address) : DelegatingHandler(new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        request.RequestUri = new Uri(address() + request.RequestUri!.PathAndQuery);
        return base.SendAsync(request, token);
    }
}
