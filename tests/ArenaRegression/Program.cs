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
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
if (root == null) throw new Exception("Workspace not found");
if (args.Contains("--probe-live-readonly"))
{
    try
    {
    var livePath = Path.Combine(root.FullName, "Server and Server selector/db.json");
    var live = JObject.Parse(File.ReadAllText(livePath));
    var people = live["Users"]!.Cast<JObject>().ToArray();
    var human = people.First(u => u["ManagedNpc"] == null);
    ulong humanId = (ulong)human["ID"]!;
    var specialNpc = people.First(u => u["ManagedNpc"] != null && (int)u["LastNormalStageCleared"]! >= 6011018 && u["Characters"]!.Count() >= 15);
    using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
    using var probe = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1/v1/") };
    async Task<T> Read<T>(ulong id, string route, IMessage request) where T : IMessage<T>, new()
    {
        string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
            .WithKey(Convert.FromBase64String((string)live["LauncherTokenKey"]!), Encryption.SymmetricKey)
            .AddClaim("userId", id).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
        using var message = new HttpRequestMessage(HttpMethod.Post, route);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Content = new ByteArrayContent(request.ToByteArray());
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
        using var response = await probe.SendAsync(message);
        response.EnsureSuccessStatusCode();
        return new MessageParser<T>(() => new T()).ParseFrom(await response.Content.ReadAsByteArrayAsync());
    }
    var entry = await Read<ResGetArena>(humanId, "arena/get", new ReqGetArena());
    var enemies = await Read<ResGetArenaEnemyList>(humanId, "arena/getenemylist", new ReqGetArenaEnemyList());
    Check(!entry.IsBan && entry.BanInfo == null && entry.User.Rank > 0 && enemies.List2.Count > 0, "live rookie arena disabled or empty");
    Check(enemies.List2.Any(r => r.User.User.Usn >= 1_500_000_000) && enemies.List2.All(r => !r.User.User.IsBot && r.Team.Slots.Count == 5), "live NPC squads invalid");
    var refreshed = await Read<ResGetUserData>(humanId, "User/Get", new ReqGetUserData { IsSimple = true });
    Check(refreshed.User.ArenaCount == entry.ArenaCount,
        $"live user refresh changed rookie attempts from {entry.ArenaCount} to {refreshed.User.ArenaCount}");
    Console.WriteLine($"PASS: live user refresh preserves {entry.ArenaCount} consumed rookie attempts");
    Console.WriteLine($"PASS: live authenticated rookie entry and {enemies.List2.Count} actual-player opponents with five-character teams");
    ulong npcId = (ulong)specialNpc["ID"]!;
    var special = await Read<ResGetSpecialArena>(npcId, "arena/special/get", new ReqGetSpecialArena());
    var rivals = await Read<ResGetSpecialArenaEnemyList>(npcId, "arena/special/getenemylist", new ReqGetSpecialArenaEnemyList());
    Check(!special.IsBan && special.BanInfo == null && special.User.Tier > 0 && rivals.List2.Count > 0 && rivals.List2.All(r => r.RoundCharacter.Count == 15), "live special roster invalid");
    foreach (var rival in rivals.List2)
    {
        var teams = await Read<ResGetSpecialArenaEnemyData>(npcId, "arena/special/getenemydata", new ReqGetSpecialArenaEnemyData { EnemyUsn = rival.User.User.Usn });
        Check(teams.Teams.Count == 3 && teams.Teams.SelectMany(t => t.Team.Slots).Select(s => s.Csn).Distinct().Count() == 15, "live special duplicates");
    }
    var ban = await Read<ResGetArenaBanInfo>(humanId, "arena/getbaninfo", new ReqGetArenaBanInfo());
    Check(ban.RookieArenaBanInfo == null && ban.SpecialArenaBanInfo == null && ban.ChampionArenaBanInfo != null, "live ban flags wrong");
    Console.WriteLine($"PASS: live special entry and {rivals.List2.Count} opponents each expose three distinct five-character teams");
    var after = JObject.Parse(File.ReadAllText(livePath));
    var afterHuman = after["Users"]!.Cast<JObject>().Single(u => (ulong)u["ID"]! == humanId);
    var beforeHuman = (JObject)human.DeepClone(); var compared = (JObject)afterHuman.DeepClone();
    foreach (string key in new[] { "Arena", "ArenaHarmonyCubeAssignments" }) { beforeHuman.Remove(key); compared.Remove(key); }
    foreach (string team in new[] { "3", "4", "12", "13" }) if (beforeHuman["UserTeams"]![team] == null) ((JObject)compared["UserTeams"]!).Remove(team);
    Check(JToken.DeepEquals(beforeHuman, compared), "read-only probe changed existing human progress/inventory");
    Check(people.Length == after["Users"]!.Count() && people.Count(u => u["ManagedNpc"] != null) == after["Users"]!.Count(u => u["ManagedNpc"] != null), "population changed");
    Console.WriteLine("PASS: probes spent no attempts or tickets and preserved existing human progress/inventory/teams");
    return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine("FAIL: live arena probe: " + exception.Message);
        return 1;
    }
}
var data = new GameData(Directory.GetFiles(Path.Combine(root.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
string save = Path.Combine(AppContext.BaseDirectory, "db.json");
bool restarting = args.Contains("--verify-saved");
if (!restarting) File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo()));
var stateProperty = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
CoreInfo State() => (CoreInfo)stateProperty.GetValue(null)!;
User Player(ulong id) => State().Users.Single(u => u.ID == id);
string conn = $"Data Source={Path.Combine(AppContext.BaseDirectory, "arena-test.db")}";
GameContext.InitializeOptions(conn, "sqlite");
var clock = new ArenaClock { Now = new DateTimeOffset(2026, 11, 6, 13, 0, 0, TimeSpan.Zero) };
using (var db = GameContext.CreateNew())
{
    db.Database.EnsureCreated();
    if (!restarting)
    {
        db.Triggers.ExecuteDelete(); db.Users.ExecuteDelete();
        ManagedNpcService.CreateBatch(db, 30, "arena-tests", clock.Now.UtcDateTime);
        var template = State().Users.OrderByDescending(u => u.LastNormalStageCleared).First();
        for (ulong id = 7001; id <= 7020; id++)
        {
            var u = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(template))!;
            u.ID = id; u.ManagedNpc = null; u.Currency.Clear(); u.LastReset = clock.Now.UtcDateTime; u.LastWeeklyReset = clock.Now.UtcDateTime;
            State().Users.Add(u); db.Users.Add(new GameUser { ID = id, Nickname = "Arena" + id, LastAction = clock.Now.UtcDateTime });
        }
        db.SaveChanges();
        File.WriteAllText(save, JsonConvert.SerializeObject(State()));
    }
}
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, 0); k.AllowSynchronousIO = true; });
builder.Services.AddSingleton<TimeProvider>(clock);
builder.Services.AddHttpContextAccessor(); builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(conn));
builder.Services.AddControllers(o => o.OutputFormatters.Insert(0, new ProtobufOutputFormatter())).AddApplicationPart(typeof(User).Assembly);
await using var app = builder.Build();
app.Use(async (ctx, next) =>
{
    if (ulong.TryParse(ctx.Request.Headers["Test-User"], out var id)) ctx.Items["UserID"] = id;
    if (ctx.Request.Headers.TryGetValue("Test-Nonce", out var n)) ctx.Items["Nonce"] = Convert.FromHexString(n.ToString());
    await next();
});
app.MapControllers();
app.MapPost("/v1/User/Get", c => new EpinelPS.LobbyServer.LobbyUser.GetUser().HandleAsync(c));
app.MapPost("/v1/wallet/get", c => new EpinelPS.LobbyServer.Wallet.GetWallet().HandleAsync(c));
app.MapPost("/v1/team/setteam", c => new EpinelPS.LobbyServer.Team.SetTeam().HandleAsync(c));
app.MapPost("/v1/mail/obtain2", c => new EpinelPS.LobbyServer.LobbyUser.Mail.Obtain2().HandleAsync(c));
app.MapPost("/v1/mail/allobtain", c => new EpinelPS.LobbyServer.LobbyUser.Mail.AllObtain().HandleAsync(c));
app.MapPost("/v1/arena/champion/get", c => new EpinelPS.LobbyServer.Arena.GetChampion().HandleAsync(c));
app.MapPost("/v1/badge/permanentcontent", c => new EpinelPS.LobbyServer.Badge.PermanentContent().HandleAsync(c));
app.MapPost("/v1/arena/legacy/getbaninfo", c => new EpinelPS.LobbyServer.Arena.GetArenaBanInfo().HandleAsync(c));
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/v1/") };
int passed = 0, failed = 0;
ulong ratingFixtureId = 8100;
if (restarting)
{
    clock.Now = new DateTimeOffset(2026, 11, 6, 13, 0, 0, TimeSpan.Zero).AddDays(14);
    await Test("restart retains settled battle receipts and ranking rewards", async () =>
    {
        var before = Player(7002).Currency.ToDictionary();
        var req = JsonConvert.DeserializeObject<ReqArenaResult>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "result-request.json")))!;
        var r = await Post<ResArenaResult>(7002, "arena/result", req, "02");
        Check(r.ArenaCount == 1 && Player(7002).Currency.OrderBy(x => x.Key).SequenceEqual(before.OrderBy(x => x.Key)), "restart replay granted rewards");
        int mails = Player(7001).MailDatas.Count;
        await Post<ResGetArena>(7001, "arena/get", new ReqGetArena());
        Check(Player(7001).MailDatas.Count == mails, "restart repeated ranking mail");
    });
    await Test("restart resumes the exact pending snapshot and settles it only once", async () =>
    {
        var saved = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pending-battle.json")));
        long target = (long)saved["target"]!;
        var started = ResArenaStart.Parser.ParseFrom(Convert.FromBase64String((string)saved["response"]!));
        var replay = await Post<ResArenaStart>(7014, "arena/start", new ReqArenaStart { EnemyUsn = target }, "71");
        Check(started.Equals(replay), "restart changed pending battle nonce/snapshot");
        var req = new ReqArenaResult { EnemyUsn = target, TeamNumber = 1, IsWin = true };
        var result = await Post<ResArenaResult>(7014, "arena/result", req, "72");
        Check(result.ArenaCount == 1 && result.Equals(await Post<ResArenaResult>(7014, "arena/result", req, "72")), "restart duplicated/lost settlement");
    });
    await Test("restart preserves purchased arena tickets and does not charge a replayed purchase", async () =>
    {
        Check(Player(7009).Currency.GetValueOrDefault(CurrencyType.ArenaTicket) == 2 &&
            Player(7009).Currency.GetValueOrDefault(CurrencyType._2nd_Arena_Ticket) == 1,
            "purchased tickets were not persisted");
        string before = JsonConvert.SerializeObject(Player(7009).Currency);
        var replay = await Post<ResBuyTicket>(7009, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 2 }, "b001");
        var receipt = ResBuyTicket.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ticket-receipt.bin")));
        Check(replay.Equals(receipt) && JsonConvert.SerializeObject(Player(7009).Currency) == before,
            "restarted purchase retry charged or granted again");
    });
    await Test("restart replays a dynamic rating receipt without changing either participant", async () =>
    {
        var saved = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rating-retry.json")));
        ulong id = (ulong)saved["attacker"]!, enemyId = (ulong)saved["defender"]!;
        var request = new ReqArenaResult { EnemyUsn = (long)enemyId, TeamNumber = 1, IsWin = true };
        var receipt = ResArenaResult.Parser.ParseFrom(Convert.FromBase64String((string)saved["response"]!));
        var replay = await Post<ResArenaResult>(id, "arena/result", request, (string)saved["nonce"]!);
        Check(replay.Equals(receipt) && Player(id).Arena!.Rating == 1745 && Player(enemyId).Arena!.Rating == 1775,
            "restart changed or repeated the dynamic rating settlement");
        Check(Player(id).Arena!.RookieRecords.Count == 1 && Player(enemyId).Arena!.RookieRecords.Count == 1,
            "restart duplicated attack or defense records");
    });
}
else
{
    await Test("unimplemented champion entry is terminally closed instead of a successful empty season", async () =>
    {
        var entry = await Post<ResGetChampionArena>(7001, "arena/champion/get", new ReqGetChampionArena());
        Check(entry.IsBan && entry.BanInfo != null, "champion entry still reports an available tournament");
        Check(entry.ChampionArenaContentsState == ChampionArenaContentsState.Closed,
            "champion entry asks the client to load or refresh an unavailable season");
        Check(entry.BanInfo.StartAt.ToDateTime() <= DateTime.UtcNow && entry.BanInfo.EndAt.ToDateTime() > DateTime.UtcNow,
            "champion closure has no active maintenance interval");
        Check(entry.BadgeData != null && entry.BadgeData.ChampionArenaContentsState == ChampionArenaContentsState.Closed,
            "champion view construction receives missing or contradictory badge data");
        Check(entry.Schedule != null && entry.Schedule.Equals(entry.BadgeData.Schedule), "entry and badge disagree on the champion schedule");
        Check(!entry.BadgeData.CanTeamSet && !entry.BadgeData.HasBetCurrentRound && !entry.BadgeData.HasUncheckedBetting,
            "unavailable champion tournament advertises playable or claimable content");
    });
    await Test("champion availability agrees across entry, badges and both ban-info paths without mutating players", async () =>
    {
        string before = JsonConvert.SerializeObject(State());
        var entry = await Post<ResGetChampionArena>(7001, "arena/champion/get", new ReqGetChampionArena());
        var badges = await Post<ResPermanentContentBadgeData>(7001, "badge/permanentcontent", new ReqPermanentContentBadgeData());
        var legacyBan = await Post<ResGetArenaBanInfo>(7001, "arena/legacy/getbaninfo", new ReqGetArenaBanInfo());
        Check(entry.BadgeData != null && entry.BadgeData.Equals(badges.ChampionArenaBadgeData), "badge refresh contradicts champion entry");
        Check(legacyBan.RookieArenaBanInfo == null && legacyBan.SpecialArenaBanInfo == null && entry.BanInfo != null &&
            entry.BanInfo.Equals(legacyBan.ChampionArenaBanInfo), "legacy ban info closes the working arenas or opens champion");
        Check(before == JsonConvert.SerializeObject(State()), "champion availability requests changed player state");
        var ban = await Post<ResGetArenaBanInfo>(7001, "arena/getbaninfo", new ReqGetArenaBanInfo());
        Check(ban.RookieArenaBanInfo == null && ban.SpecialArenaBanInfo == null && entry.BanInfo.Equals(ban.ChampionArenaBanInfo),
            "controller ban info contradicts champion entry");
    });
    await Test("rookie entry is enabled with persisted five-character defense", async () =>
    {
        var r = await Post<ResGetArena>(7001, "arena/get", new ReqGetArena());
        Check(!r.IsBan && r.BanInfo == null && r.User.Rank > 0 && r.ArenaCount == 0, "rookie still disabled or invalid entry");
        Check(r.DefenseTeam.Teams.Single().Slots.Count(s => s.Value > 0) == 5, "missing defense");
    });
    await Test("both arenas expose NPC opponents, valid teams, rankings and profiles", async () =>
    {
        var normal = await Post<ResGetArenaEnemyList>(7001, "arena/getenemylist", new ReqGetArenaEnemyList());
        Check(normal.List2.Count is > 0 and <= 3 && normal.List2.All(x => x.User.User.Usn != 7001 && x.Team.Slots.Count == 5), "invalid normal rivals");
        Check(normal.List2.Any(x => x.User.User.Usn >= 1_500_000_000), "managed NPCs not matched");
        var special = await Post<ResGetSpecialArena>(7001, "arena/special/get", new ReqGetSpecialArena());
        Check(!special.IsBan && special.User.Rank > 0 && special.User.Tier > 0 && special.SpecialArenaCount == 0, "special not enabled");
        var rivals = await Post<ResGetSpecialArenaEnemyList>(7001, "arena/special/getenemylist", new ReqGetSpecialArenaEnemyList());
        Check(rivals.List2.Count > 0 && rivals.List2.All(x => x.RoundCharacter.Count == 15 && x.TotalCombat > 0), "special squads malformed");
        var enemy = await Post<ResGetSpecialArenaEnemyData>(7001, "arena/special/getenemydata", new ReqGetSpecialArenaEnemyData { EnemyUsn = rivals.List2[0].User.User.Usn });
        Check(enemy.Teams.Count == 3 && enemy.Teams.SelectMany(x => x.Team.Slots).Select(x => x.Csn).Distinct().Count() == 15, "special duplicated characters");
        Check((await Post<ResGetArenaTopRank>(7001, "arena/toprank", new ReqGetArenaTopRank())).RankList.Count > 0, "empty rank list");
    });
    await Test("arena team validation rejects foreign characters and preserves manual presets", async () =>
    {
        var valid = Player(7001).UserTeams[3].Teams[0].Clone();
        (valid.Slots[0].Value, valid.Slots[1].Value) = (valid.Slots[1].Value, valid.Slots[0].Value);
        await Post<ResSetTeam>(7001, "team/setteam", new ReqSetTeam { Type = 3, Teams = { valid } });
        var bad = valid.Clone(); bad.Slots[0].Value = -1;
        await Status(7001, "team/setteam", new ReqSetTeam { Type = 3, Teams = { bad } }, 400);
        await Post<ResGetArena>(7001, "arena/get", new ReqGetArena());
        Check(Player(7001).UserTeams[3].Teams[0].Equals(valid), "manual team overwritten");
        var dup = Player(7001).UserTeams[12].Clone(); dup.Teams[1].Slots[0].Value = dup.Teams[0].Slots[0].Value;
        await Status(7001, "team/setteam", new ReqSetTeam { Type = 12, Teams = { dup.Teams } }, 400);
    });
    await Test("rookie start and result are idempotent and write both players' records", async () =>
    {
        var target = await Target(7002, false);
        var begin = await Post<ResArenaStart>(7002, "arena/start", new ReqArenaStart { EnemyUsn = target }, "01");
        Check(begin.Enemy.Character.Count == 5 && begin.Nonce != 0, "missing battle snapshot");
        Check(begin.Equals(await Post<ResArenaStart>(7002, "arena/start", new ReqArenaStart { EnemyUsn = target }, "01")), "start retry changed snapshot");
        var request = new ReqArenaResult { EnemyUsn = target, TeamNumber = 1, IsWin = true };
        var r = await Post<ResArenaResult>(7002, "arena/result", request, "02");
        Check(r.ArenaCount == 1 && r.ArenaResult.User.AfterRating > r.ArenaResult.User.BeforeRating && r.Reward.Currency.Count > 0, "no paid settlement");
        long chips = Player(7002).Currency.GetValueOrDefault(CurrencyType.ArenaChip);
        Check(r.Equals(await Post<ResArenaResult>(7002, "arena/result", request, "02")) && chips == Player(7002).Currency.GetValueOrDefault(CurrencyType.ArenaChip), "double settlement");
        Check((await Post<ResGetArenaRecordList>(7002, "arena/getrecordlist", new ReqGetArenaRecordList())).List.Count == 1, "missing attack record");
        Check((await Post<ResGetArenaRecordList>((ulong)target, "arena/getrecordlist", new ReqGetArenaRecordList())).List.Any(x => !x.IsAttack && !x.IsWin), "missing defense loss");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result-request.json"), JsonConvert.SerializeObject(request));
    });
    await Test("rookie allows five free battles before requiring tickets and validates the active start", async () =>
    {
        var target = await Target(7003, false);
        await Status(7003, "arena/result", new ReqArenaResult { EnemyUsn = target, TeamNumber = 1, IsWin = true }, 409);
        for (int i = 0; i < 5; i++)
        {
            await Post<ResArenaStart>(7003, "arena/start", new ReqArenaStart { EnemyUsn = target });
            await Status(7003, "arena/result", new ReqArenaResult { EnemyUsn = target + 100, TeamNumber = 1, IsWin = true }, 409);
            var r = await Post<ResArenaResult>(7003, "arena/result", new ReqArenaResult { EnemyUsn = target, TeamNumber = 1, IsWin = i != 2 });
            Check(r.ArenaCount == i + 1, "same-target normal battle swallowed");
            var refreshed = await Post<ResGetUserData>(7003, "User/Get", new ReqGetUserData { IsSimple = i % 2 == 0 });
            Check(refreshed.User.ArenaCount == i + 1,
                $"user refresh reset rookie attempts after battle {i + 1}: {refreshed.User.ArenaCount}");
        }
        await Status(7003, "arena/start", new ReqArenaStart { EnemyUsn = target }, 400);
        Check(Player(7003).Arena!.RookieCount == 5 && Player(7003).Arena!.Pending == null,
            "rejected sixth battle changed attempts or created a pending match");
        Player(7003).Currency[CurrencyType.FreeCash] = 100;
        await Post<ResBuyTicket>(7003, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 1 });
        Check(Player(7003).Currency.GetValueOrDefault(CurrencyType.ArenaTicket) == 1 && Player(7003).Arena!.RookieCount == 5,
            "purchasing a ticket did not add one ticket or changed consumed attempts");
        await Post<ResArenaStart>(7003, "arena/start", new ReqArenaStart { EnemyUsn = target });
        Check(Player(7003).Currency[CurrencyType.ArenaTicket] == 0 && Player(7003).Arena!.RookieCount == 6,
            "sixth battle did not consume exactly one ticket and attempt");
    });
    await Test("arena ticket purchases charge configured gems and return final wallet balances", async () =>
    {
        var user = Player(7009);
        user.Currency[CurrencyType.FreeCash] = 300;
        user.Currency[CurrencyType.ChargeCash] = 20;
        var purchase = await Post<ResBuyTicket>(7009, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 2 }, "b001");
        Check(user.Currency[CurrencyType.FreeCash] == 100 && user.Currency[CurrencyType.ChargeCash] == 20 &&
            user.Currency.GetValueOrDefault(CurrencyType.ArenaTicket) == 2, "rookie purchase did not exchange 200 free gems for two tickets");
        Check(purchase.Currencies.Single(c => c.Type == 99).Value == 100 &&
            purchase.Currencies.Single(c => c.Type == 6001).Value == 2, "purchase response omitted final gem/ticket balances");
        File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "ticket-receipt.bin"), purchase.ToByteArray());
        var special = await Post<ResBuyTicket>(7009, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6002, Count = 1 }, "b002");
        Check(user.Currency[CurrencyType.FreeCash] == 0 && user.Currency[CurrencyType.ChargeCash] == 20 &&
            special.Currencies.Single(c => c.Type == 6002).Value == 1, "special purchase used the wrong ticket or price");
        var wallet = await Post<ResGetCurrencyData>(7009, "wallet/get", new ReqGetCurrencyData());
        Check(wallet.Currency.Single(c => c.Type == 6001).Value == 2 && wallet.Currency.Single(c => c.Type == 6002).Value == 1,
            "wallet refresh lost purchased tickets");
        var saved = JObject.Parse(File.ReadAllText(save))["Users"]!.Single(u => (ulong)u["ID"]! == 7009);
        Check((long)saved["Currency"]!["ArenaTicket"]! == 2 && (long)saved["Currency"]!["_2nd_Arena_Ticket"]! == 1,
            "purchase was acknowledged before saving the tickets");
    });
    await Test("arena ticket purchases spend free gems first and accept an exact paid-gem remainder", async () =>
    {
        var user = Player(7011);
        user.Currency[CurrencyType.FreeCash] = 40;
        user.Currency[CurrencyType.ChargeCash] = 60;
        var purchase = await Post<ResBuyTicket>(7011, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 1 });
        Check(purchase.Currencies.Single(c => c.Type == 99).Value == 0 && purchase.Currencies.Single(c => c.Type == 98).Value == 0 &&
            user.Currency.GetValueOrDefault(CurrencyType.ArenaTicket) == 1, "free/paid split or exact paid balance was wrong");
        string before = JsonConvert.SerializeObject(user.Currency);
        await Status(7011, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 1 }, 400);
        Check(JsonConvert.SerializeObject(user.Currency) == before, "insufficient gems partially charged or granted a ticket");
    });
    await Test("invalid arena ticket purchases cannot spend gems or mint unrelated currencies", async () =>
    {
        string before = JsonConvert.SerializeObject(Player(7011).Currency);
        await Status(0, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 1 }, 401);
        await Status(7011, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 5000, Count = 1 }, 400);
        await Status(7011, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 0 }, 400);
        await Status(7011, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = -1 }, 400);
        using var noNonce = await Send(7011, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 1 }, "");
        Check((int)noNonce.StatusCode == 400 && JsonConvert.SerializeObject(Player(7011).Currency) == before,
            "invalid or nonce-less purchase changed the wallet");
    });
    await Test("concurrent retries of an arena ticket purchase charge and grant exactly once", async () =>
    {
        Player(7013).Currency[CurrencyType.FreeCash] = 100;
        var request = new ReqBuyTicket { CurrencyType = 6001, Count = 1 };
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Post<ResBuyTicket>(7013, "wallet/buyticket", request, "b013")));
        Check(responses.All(r => r.Equals(responses[0])) && Player(7013).Currency[CurrencyType.FreeCash] == 0 &&
            Player(7013).Currency.GetValueOrDefault(CurrencyType.ArenaTicket) == 1, "concurrent retries duplicated the charge or ticket");
        using var reused = await Send(7013, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 2 }, "b013");
        Check((int)reused.StatusCode == 409 && Player(7013).Currency.GetValueOrDefault(CurrencyType.ArenaTicket) == 1,
            "a reused purchase nonce accepted a different quantity");
    });
    await Test("failed arena ticket saves roll back the charge and permit one successful retry", async () =>
    {
        var user = Player(7015);
        user.Currency[CurrencyType.FreeCash] = 100;
        string before = JsonConvert.SerializeObject(State());
        Directory.CreateDirectory(save + ".tmp");
        try
        {
            using var failedSave = await Send(7015, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 1 }, "b015");
            Check((int)failedSave.StatusCode == 500, "purchase acknowledged a failed save");
        }
        finally { Directory.Delete(save + ".tmp"); }
        Check(JsonConvert.SerializeObject(State()) == before, "failed purchase save left a partial wallet or receipt");
        await Post<ResBuyTicket>(7015, "wallet/buyticket", new ReqBuyTicket { CurrencyType = 6001, Count = 1 }, "b015");
        Check(user.Currency[CurrencyType.FreeCash] == 0 && user.Currency.GetValueOrDefault(CurrencyType.ArenaTicket) == 1,
            "purchase could not recover after the failed save");
    });
    await Test("special best-of-three validates rounds and swaps unique ranks", async () =>
    {
        var before = await Post<ResGetSpecialArena>(7010, "arena/special/get", new ReqGetSpecialArena());
        var target = await Target(7010, true);
        var begin = await Post<ResSpecialArenaStart>(7010, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = target });
        Check(begin.SimulationTarget == SpecialArenaSimulationTarget.Client && begin.Enemy.Character.Count == 15, "not client simulation");
        await Status(7010, "arena/special/result", new ReqSpecialArenaResult { EnemyUsn = target, Result = { new NetSpecialArenaRoundResult { Round = 1, IsWin = true }, new NetSpecialArenaRoundResult { Round = 1, IsWin = true } } }, 400);
        var result = await Post<ResSpecialArenaResult>(7010, "arena/special/result", new ReqSpecialArenaResult { EnemyUsn = target, Result = { new NetSpecialArenaRoundResult { Round = 1, IsWin = true }, new NetSpecialArenaRoundResult { Round = 2, IsWin = true } } });
        Check(result.SpecialArenaCount == 1 && result.Battle.User.After.Rank <= before.User.Rank, "rank exchange incorrect");
        var refreshed = await Post<ResGetUserData>(7010, "User/Get", new ReqGetUserData());
        Check(refreshed.User.SpecialArenaCount == 1, "user refresh reset special arena attempts");
        var top = await Post<ResGetSpecialArenaTopRank>(7010, "arena/special/toprank", new ReqGetSpecialArenaTopRank());
        Check(top.RankList.Select(x => x.Rank).Distinct().Count() == top.RankList.Count, "duplicate special ranks");
    });
    await Test("unlocked content, session, invalid enemies and unsupported simulation are checked", async () =>
    {
        await Status(0, "arena/get", new ReqGetArena(), 401);
        await Status(7001, "arena/start", new ReqArenaStart { EnemyUsn = 7001 }, 400);
        await Status(7001, "arena/start", new ReqArenaStart { EnemyUsn = -1 }, 400);
        Player(7008).LastNormalStageCleared = 6000001; Player(7008).FieldInfoNew.Clear();
        await Status(7008, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = await Target(7001, true) }, 403);
        await Status(7001, "arena/special/simulation/result", new ReqSpecialArenaResultV2 { EnemyUsn = 1 }, 400);
    });
    await Test("special timed rewards accrue once, claims are idempotent and time cannot rewind", async () =>
    {
        await Post<ResGetSpecialArena>(7004, "arena/special/get", new ReqGetSpecialArena());
        clock.Now = clock.Now.AddHours(1);
        var show = await Post<ResShowSpecialArenaReward>(7004, "arena/special/showreward", new ReqShowSpecialArenaReward());
        Check(show.Reward.Currency.Any(x => x.Type == 5000 && x.Value > 0), "time rewards did not accrue");
        long old = Player(7004).Currency.GetValueOrDefault(CurrencyType.ArenaChip);
        var claim = await Post<ResObtainSpecialArenaReward>(7004, "arena/special/obtainreward", new ReqObtainSpecialArenaReward(), "41");
        long balance = Player(7004).Currency.GetValueOrDefault(CurrencyType.ArenaChip);
        Check(balance > old && claim.Equals(await Post<ResObtainSpecialArenaReward>(7004, "arena/special/obtainreward", new ReqObtainSpecialArenaReward(), "41")), "claim retry failed");
        clock.Now = clock.Now.AddMinutes(-20);
        await Post<ResObtainSpecialArenaReward>(7004, "arena/special/obtainreward", new ReqObtainSpecialArenaReward());
        Check(Player(7004).Currency.GetValueOrDefault(CurrencyType.ArenaChip) == balance, "backward clock minted rewards");
        clock.Now = clock.Now.AddMinutes(20);
    });
    await Test("concurrent starts charge one pending battle and disk failure rolls back", async () =>
    {
        var target = await Target(7005, false);
        var begins = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Post<ResArenaStart>(7005, "arena/start", new ReqArenaStart { EnemyUsn = target }, "51")));
        Check(begins.All(x => x.Equals(begins[0])), "concurrent starts differed");
        string before = JsonConvert.SerializeObject(State());
        Directory.CreateDirectory(save + ".tmp");
        try { await Status(7005, "arena/result", new ReqArenaResult { EnemyUsn = target, TeamNumber = 1, IsWin = true }, 500); }
        finally { Directory.Delete(save + ".tmp"); }
        Check(JsonConvert.SerializeObject(State()) == before, "save failure left partial rank/currency state");
        await Post<ResArenaResult>(7005, "arena/result", new ReqArenaResult { EnemyUsn = target, TeamNumber = 1, IsWin = true });
    });
    await Test("refresh rotates valid managed opponents and underfilled special squads stay excluded", async () =>
    {
        var a = await Post<ResGetArenaEnemyList>(7012, "arena/getenemylist", new ReqGetArenaEnemyList());
        var b = await Post<ResGetArenaEnemyList>(7012, "arena/getenemylist", new ReqGetArenaEnemyList());
        Check(!a.List2.Select(r => r.User.User.Usn).SequenceEqual(b.List2.Select(r => r.User.User.Usn)), "refresh always returns the same opponents");
        foreach (var npc in State().Users.Where(u => u.ManagedNpc != null && u.Characters.Count < 15))
            await Status(7012, "arena/special/getenemydata", new ReqGetSpecialArenaEnemyData { EnemyUsn = (long)npc.ID }, 404);
    });
    await Test("nonce-less delayed results cannot settle a second battle against the same opponent", async () =>
    {
        long target = await Target(7018, false);
        var req = new ReqArenaResult { EnemyUsn = target, TeamNumber = 1, IsWin = true };
        await Post<ResArenaStart>(7018, "arena/start", new ReqArenaStart { EnemyUsn = target });
        await Post<ResArenaResult>(7018, "arena/result", req);
        await Post<ResArenaStart>(7018, "arena/start", new ReqArenaStart { EnemyUsn = target });
        long chips = Player(7018).Currency.GetValueOrDefault(CurrencyType.ArenaChip);
        using var response = await Send(7018, "arena/result", req, "");
        Check((int)response.StatusCode == 400 && Player(7018).Arena!.Pending != null && chips == Player(7018).Currency.GetValueOrDefault(CurrencyType.ArenaChip), "old nonce-less body settled the second battle");
    });
    await Test("receipt pressure cannot turn an old encrypted result into a fresh battle result", async () =>
    {
        var request = JsonConvert.DeserializeObject<ReqArenaResult>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "result-request.json")))!;
        for (int i = 0; i < 130; i++) await Post<ResObtainSpecialArenaReward>(7002, "arena/special/obtainreward", new ReqObtainSpecialArenaReward());
        await Post<ResArenaStart>(7002, "arena/start", new ReqArenaStart { EnemyUsn = request.EnemyUsn });
        long chips = Player(7002).Currency.GetValueOrDefault(CurrencyType.ArenaChip);
        using var response = await Send(7002, "arena/result", request, "02");
        Check((response.IsSuccessStatusCode || (int)response.StatusCode == 409) && Player(7002).Arena!.Pending != null && chips == Player(7002).Currency.GetValueOrDefault(CurrencyType.ArenaChip), "evicted result settled a newer battle");
    });
    await Test("a team save using an account replaced by reload returns a stale-session error", () =>
    {
        var old = Player(7015); int index = State().Users.IndexOf(old);
        var current = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(old))!;
        State().Users[index] = current;
        var method = typeof(User).Assembly.GetType("EpinelPS.LobbyServer.Arena.ArenaTeams")!.GetMethod("Save", BindingFlags.Static | BindingFlags.NonPublic)!;
        try { method.Invoke(null, [old, new ReqSetTeam { Type = 3, Teams = { old.UserTeams[3].Teams[0].Clone() } }]); }
        catch (TargetInvocationException e) when (e.InnerException is Microsoft.AspNetCore.Http.BadHttpRequestException { StatusCode: 409 }) { return Task.CompletedTask; }
        throw new Exception("stale team save was accepted");
    });
    await Test("special rank swaps accrue old and new tier points on the correct side of the battle", async () =>
    {
        var before = await Post<ResGetSpecialArena>(7020, "arena/special/get", new ReqGetSpecialArena());
        var top = await Post<ResGetSpecialArenaTopRank>(7020, "arena/special/toprank", new ReqGetSpecialArenaTopRank());
        long target = top.RankList.First(x => x.Rank < before.User.Rank).User.Usn;
        decimal points = Player(7020).Arena!.PointScaled;
        int oldRate = data.ArenaTimeRewards[before.User.Tier].TimeStackReward.Single(r => (int)r.ItemType == 9).ItemValue;
        await Post<ResSpecialArenaStart>(7020, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = target });
        clock.Now = clock.Now.AddMinutes(5);
        var result = await Post<ResSpecialArenaResult>(7020, "arena/special/result", new ReqSpecialArenaResult { EnemyUsn = target,
            Result = { new NetSpecialArenaRoundResult { Round = 1, IsWin = true }, new NetSpecialArenaRoundResult { Round = 2, IsWin = true } } });
        int newRate = data.ArenaTimeRewards[result.Battle.User.After.Tier].TimeStackReward.Single(r => (int)r.ItemType == 9).ItemValue;
        clock.Now = clock.Now.AddMinutes(5);
        await Post<ResGetSpecialArena>(7020, "arena/special/get", new ReqGetSpecialArena());
        Check(Player(7020).Arena!.PointScaled - points == 5m * oldRate + 5m * newRate, "tier change applied retroactively");
    });
    await Test("late entrants cannot alter or receive ranking mail for past days", async () =>
    {
        var top = await Post<ResGetArenaTopRank>(7001, "arena/toprank", new ReqGetArenaTopRank());
        var old = top.RankList.First(r => r.User.Usn < 10000 && r.Rating == 1000 && data.ArenaRewards.Values.Any(x => x.MaxRank == r.Rank));
        int rewardId = data.ArenaRewards.Values.Single(r => old.Rank >= r.MinRank && (r.MaxRank == 0 || old.Rank <= r.MaxRank)).DailyRewardId;
        var late = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(Player(7006)))!;
        late.ID = 1; late.Arena = null; late.ManagedNpc = null; late.MailDatas.Clear();
        State().Users.Add(late);
        using (var db = GameContext.CreateNew()) { db.Users.Add(new GameUser { ID = 1, Nickname = "Late", LastAction = clock.Now.UtcDateTime }); db.SaveChanges(); }
        clock.Now = clock.Now.AddDays(2);
        await Post<ResGetArena>(7001, "arena/get", new ReqGetArena());
        var mail = Player((ulong)old.User.Usn).MailDatas.Values.First(m => m.Title.Str == "新手竞技场每日排名奖励");
        var expected = data.RewardDataRecords[rewardId].Rewards.Select(r => ((int)r.RewardType, r.RewardId, r.RewardValue));
        Check(mail.Items.Select(r => (r.RewardType, r.RewardId, r.RewardValue)).SequenceEqual(expected), "late entrant changed historical rank rewards");
        Check(Player(1).MailDatas.Count == 0, "late entrant received pre-enrollment rewards");
    });
    await Test("special attempts, daily refill, timeout and seasonal boundaries cannot replay a battle", async () =>
    {
        long target = await Target(7016, true);
        for (int i = 0; i < 2; i++)
        {
            await Post<ResSpecialArenaStart>(7016, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = target });
            await Post<ResSpecialArenaResult>(7016, "arena/special/result", new ReqSpecialArenaResult { EnemyUsn = target,
                Result = { new NetSpecialArenaRoundResult { Round = 1 }, new NetSpecialArenaRoundResult { Round = 2 } } });
        }
        await Status(7016, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = target }, 400);
        Player(7016).Currency[CurrencyType._2nd_Arena_Ticket] = 1;
        await Post<ResSpecialArenaStart>(7016, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = target });
        Check(Player(7016).Currency[CurrencyType._2nd_Arena_Ticket] == 0, "special paid ticket not spent");
        clock.Now = clock.Now.AddMinutes(16);
        await Status(7016, "arena/special/result", new ReqSpecialArenaResult { EnemyUsn = target,
            Result = { new NetSpecialArenaRoundResult { Round = 1 }, new NetSpecialArenaRoundResult { Round = 2 } } }, 409);
        clock.Now = new DateTimeOffset(clock.Now.UtcDateTime.Date.AddHours(20).AddMinutes(1), TimeSpan.Zero);
        var refreshed = await Post<ResGetUserData>(7016, "User/Get", new ReqGetUserData { IsSimple = true });
        Check(refreshed.User.ArenaCount == 0 && refreshed.User.SpecialArenaCount == 0,
            "user refresh exposed yesterday's arena attempts before re-entering arena");
        Check((await Post<ResGetSpecialArena>(7016, "arena/special/get", new ReqGetSpecialArena())).SpecialArenaCount == 0, "daily attempts not reset");
        clock.Now = new DateTimeOffset(2026, 11, 19, 19, 58, 0, TimeSpan.Zero);
        await Post<ResSpecialArenaStart>(7016, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = target });
        clock.Now = clock.Now.AddMinutes(3);
        await Status(7016, "arena/special/result", new ReqSpecialArenaResult { EnemyUsn = target,
            Result = { new NetSpecialArenaRoundResult { Round = 1, IsWin = true }, new NetSpecialArenaRoundResult { Round = 2, IsWin = true } } }, 409);
        var reward = await Post<ResShowSpecialArenaReward>(7006, "arena/special/showreward", new ReqShowSpecialArenaReward());
        Check(reward.Reward.Currency.Single(x => x.Type == 5000).Value <= 9999 && Player(7006).Arena!.BoxTicks <= TimeSpan.TicksPerDay, "offline box exceeded cap");
    });
    await Test("user refresh preserves consumed attempts if the clock rewinds across daily reset", async () =>
    {
        var target = await Target(7019, false);
        await Post<ResArenaStart>(7019, "arena/start", new ReqArenaStart { EnemyUsn = target });
        await Post<ResArenaResult>(7019, "arena/result", new ReqArenaResult { EnemyUsn = target, TeamNumber = 1 });
        var present = clock.Now;
        try
        {
            clock.Now = clock.Now.AddMinutes(-2);
            var refreshed = await Post<ResGetUserData>(7019, "User/Get", new ReqGetUserData { IsSimple = true });
            var entry = await Post<ResGetArena>(7019, "arena/get", new ReqGetArena());
            Check(entry.ArenaCount == 1 && refreshed.User.ArenaCount == 1,
                "clock rollback changed the user snapshot count while arena kept the consumed attempt");
        }
        finally { clock.Now = present; }
    });
    await Test("daily and season ranking mail are issued once, and mail cannot pay twice", async () =>
    {
        clock.Now = new DateTimeOffset(2026, 11, 20, 13, 0, 0, TimeSpan.Zero);
        await Post<ResGetArena>(7001, "arena/get", new ReqGetArena());
        var mails = Player(7001).MailDatas.Values.Where(x => x.Title.Str.Contains("竞技场")).ToArray();
        Check(mails.Length >= 3, "daily/season ranking rewards absent");
        int count = mails.Length;
        await Post<ResGetArena>(7001, "arena/get", new ReqGetArena());
        Check(Player(7001).MailDatas.Values.Count(x => x.Title.Str.Contains("竞技场")) == count, "duplicate ranking mail");
        var request = new ReqObtainMail2 { Msn = mails[0].Msn };
        await Post<ResObtainMail2>(7001, "mail/obtain2", request);
        var balance = JsonConvert.SerializeObject(Player(7001).Currency);
        await Post<ResObtainMail2>(7001, "mail/obtain2", request);
        Check(JsonConvert.SerializeObject(Player(7001).Currency) == balance, "mail paid twice");
    });
    await Test("rank mail claims restore attachments and balances after a failed save", async () =>
    {
        var mail = Player(7007).MailDatas.Values.First(x => x.State == 1);
        string before = JsonConvert.SerializeObject(Player(7007));
        Directory.CreateDirectory(save + ".tmp");
        try { await Status(7007, "mail/obtain2", new ReqObtainMail2 { Msn = mail.Msn }, 500); }
        finally { Directory.Delete(save + ".tmp"); }
        Check(JsonConvert.SerializeObject(Player(7007)) == before, "mail save failure changed inventory/state");
        await Post<ResAllObtainMail>(7007, "mail/allobtain", new ReqAllObtainMail());
        var wallet = JsonConvert.SerializeObject(Player(7007).Currency);
        await Post<ResObtainMail2>(7007, "mail/obtain2", new ReqObtainMail2 { Msn = mail.Msn });
        Check(JsonConvert.SerializeObject(Player(7007).Currency) == wallet, "single claim paid an all-claimed mail");
    });
    await Test("mail reward task triggers wait for the durable grant and cannot duplicate on retry", async () =>
    {
        int bgm = data.jukeboxListDataRecords.Keys.First(k => !Player(7017).JukeboxBgm.Contains(k));
        long msn = 900000001;
        Player(7017).MailDatas[msn] = new NetUserMailData { Msn = msn, State = 1, HasReward = true, Title = new() { Str = "BGM" },
            Items = { new NetMailRewardItem { RewardType = (int)RewardType.Bgm, RewardId = bgm, RewardValue = 1, ExpiredAt = DateTime.UtcNow.AddDays(30).Ticks } } };
        using var db = GameContext.CreateNew();
        int count = db.Triggers.Count(t => t.UserId == 7017 && t.Type == Trigger.ObtainJukeboxTheme);
        Directory.CreateDirectory(save + ".tmp");
        try { await Status(7017, "mail/obtain2", new ReqObtainMail2 { Msn = msn }, 500); }
        finally { Directory.Delete(save + ".tmp"); }
        Check(db.Triggers.Count(t => t.UserId == 7017 && t.Type == Trigger.ObtainJukeboxTheme) == count && !Player(7017).JukeboxBgm.Contains(bgm), "uncommitted mail wrote task triggers");
        await Post<ResObtainMail2>(7017, "mail/obtain2", new ReqObtainMail2 { Msn = msn });
        await Post<ResObtainMail2>(7017, "mail/obtain2", new ReqObtainMail2 { Msn = msn });
        Check(db.Triggers.Count(t => t.UserId == 7017 && t.Type == Trigger.ObtainJukeboxTheme) == count + 1, "mail retry duplicated task trigger");
    });
    await Test("a pending battle persists its exact snapshot for the next process", async () =>
    {
        long target = await Target(7014, false);
        var start = await Post<ResArenaStart>(7014, "arena/start", new ReqArenaStart { EnemyUsn = target }, "71");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "pending-battle.json"), JsonConvert.SerializeObject(new { target, response = Convert.ToBase64String(start.ToByteArray()) }));
        var saved = JObject.Parse(File.ReadAllText(save));
        var pending = saved["Users"]!.Single(u => (ulong)u["ID"]! == 7014)["Arena"]!["Pending"]!;
        Check((long)pending["EnemyId"]! == target && Convert.FromBase64String((string)pending["Enemy"]!).SequenceEqual(start.Enemy.ToByteArray()), "pending snapshot missing from disk");
    });
    foreach (var sample in new (string Name, int Attacker, int Defender, bool Won, int AfterAttacker, int AfterDefender, bool NpcAttack, bool NpcDefense)[]
    {
        ("equal ratings", 1000, 1000, true, 1020, 980, false, false),
        ("underdog win", 1720, 1800, true, 1745, 1775, false, true),
        ("underdog loss", 1720, 1800, false, 1705, 1815, false, true),
        ("NPC favorite win", 1800, 1720, true, 1815, 1705, true, false),
        ("NPC favorite loss", 1800, 1720, false, 1775, 1745, true, false),
        ("NPC upset at a 400 point gap", 1000, 1400, true, 1036, 1364, true, true),
        ("NPC favorite at a 400 point gap", 1400, 1000, true, 1404, 996, true, true),
        ("zero floor", 10, 10, true, 30, 0, false, false),
        ("extreme upset", 0, int.MaxValue, true, 40, int.MaxValue - 40, false, true),
        ("integer ceiling", int.MaxValue, int.MaxValue, true, int.MaxValue, int.MaxValue - 20, true, false),
        ("minimum transfer", 2000, 0, true, 2001, 0, false, true)
    })
    {
        await Test("rookie dynamic rating: " + sample.Name, async () =>
        {
            var (attacker, defender) = await RatingPair(sample.NpcAttack, sample.NpcDefense);
            attacker.Arena!.Rating = sample.Attacker;
            defender.Arena!.Rating = sample.Defender;
            await Post<ResArenaStart>(attacker.ID, "arena/start", new ReqArenaStart { EnemyUsn = (long)defender.ID });
            var request = new ReqArenaResult { EnemyUsn = (long)defender.ID, TeamNumber = 1, IsWin = sample.Won };
            string nonce = Guid.NewGuid().ToString("N");
            var result = await Post<ResArenaResult>(attacker.ID, "arena/result", request, nonce);
            Check(result.ArenaResult.User.BeforeRating == sample.Attacker && result.ArenaResult.Enemy.BeforeRating == sample.Defender &&
                result.ArenaResult.User.AfterRating == sample.AfterAttacker && result.ArenaResult.Enemy.AfterRating == sample.AfterDefender,
                $"expected {sample.AfterAttacker}/{sample.AfterDefender}, got {result.ArenaResult.User.AfterRating}/{result.ArenaResult.Enemy.AfterRating}");
            Check(attacker.Arena.Rating == sample.AfterAttacker && defender.Arena.Rating == sample.AfterDefender,
                "response and participant ratings disagree");
            var attack = NetRookieArenaRecordData.Parser.ParseFrom(attacker.Arena.RookieRecords.Single());
            var defense = NetRookieArenaRecordData.Parser.ParseFrom(defender.Arena.RookieRecords.Single());
            Check(attack.IsAttack && !defense.IsAttack && attack.IsWin == sample.Won && defense.IsWin != sample.Won &&
                attack.BeforeRating == sample.Attacker && attack.AfterRating == sample.AfterAttacker &&
                defense.BeforeRating == sample.Defender && defense.AfterRating == sample.AfterDefender && attack.MatchId == defense.MatchId,
                "attack/defense history disagrees with the actual rating transfer");
            var persisted = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))!;
            Check(persisted.Users.Single(u => u.ID == attacker.ID).Arena!.Rating == sample.AfterAttacker &&
                persisted.Users.Single(u => u.ID == defender.ID).Arena!.Rating == sample.AfterDefender, "ratings were not durably saved");
            var replay = await Post<ResArenaResult>(attacker.ID, "arena/result", request, nonce);
            Check(replay.Equals(result) && attacker.Arena.Rating == sample.AfterAttacker && defender.Arena.Rating == sample.AfterDefender &&
                attacker.Arena.RookieRecords.Count == 1 && defender.Arena.RookieRecords.Count == 1, "retry applied the transfer twice");
            if (sample.Name == "underdog win")
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "rating-retry.json"), JsonConvert.SerializeObject(new
                { attacker = attacker.ID, defender = defender.ID, nonce, response = Convert.ToBase64String(result.ToByteArray()) }));
        });
    }
    await Test("rookie rating uses both current ratings when a started battle settles", async () =>
    {
        var (attacker, defender) = await RatingPair(false, true);
        await Post<ResArenaStart>(attacker.ID, "arena/start", new ReqArenaStart { EnemyUsn = (long)defender.ID });
        attacker.Arena!.Rating = 1720; defender.Arena!.Rating = 1800;
        var result = await Post<ResArenaResult>(attacker.ID, "arena/result", new ReqArenaResult { EnemyUsn = (long)defender.ID, TeamNumber = 1, IsWin = true });
        Check(result.ArenaResult.User.BeforeRating == 1720 && result.ArenaResult.Enemy.BeforeRating == 1800 &&
            attacker.Arena.Rating == 1745 && defender.Arena.Rating == 1775, "settlement used stale ratings or updated one side before calculating the other");
    });
    await Test("autonomous NPC rookie matches use the same dynamic rating transfer", async () =>
    {
        var enabled = State().Users.Where(u => u.ManagedNpc != null).ToDictionary(u => u.ID, u => u.ManagedNpc!.Enabled);
        try
        {
            foreach (var id in enabled.Keys) Player(id).ManagedNpc!.Enabled = false;
            var (attacker, defender) = await RatingPair(true, true);
            attacker.Arena!.Rating = 1720; defender.Arena!.Rating = 1800;
            attacker.Arena.RookieCount = 4; attacker.Arena.SpecialCount = 2;
            var assembly = typeof(User).Assembly;
            var scopeType = assembly.GetType("EpinelPS.Utils.RewardMutationScope")!;
            var serviceType = assembly.GetType("EpinelPS.LobbyServer.Arena.ArenaService")!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            using var scope = (IDisposable)Activator.CreateInstance(scopeType, flags, null, [attacker, clock.Now.UtcDateTime], null)!;
            var service = Activator.CreateInstance(serviceType, flags, null, [attacker, clock.Now.UtcDateTime, null], null)!;
            int played = (int)serviceType.GetMethod("RunNpcRoutine", flags)!.Invoke(service, null)!;
            Check(played == 1 && attacker.Arena.RookieCount == 5 && attacker.Arena.Rating == 1745 && defender.Arena.Rating == 1775,
                $"NPC routine did not share rating settlement: {played} matches, {attacker.Arena.Rating}/{defender.Arena.Rating}");
            Check(NetRookieArenaRecordData.Parser.ParseFrom(defender.Arena.RookieRecords.Single()).AfterRating == 1775,
                "autonomous defense record lost the dynamic rating");
        }
        finally
        {
            foreach (var entry in enabled) Player(entry.Key).ManagedNpc!.Enabled = entry.Value;
        }
    });
}
if (!restarting)
{
    foreach (var scenario in new[]
    {
        (Name: "two wins", Wins: new[] { true, true }, Won: true),
        (Name: "two losses", Wins: new[] { false, false }, Won: false),
        (Name: "three-round win", Wins: new[] { false, true, true }, Won: true),
        (Name: "three-round loss", Wins: new[] { true, false, false }, Won: false)
    })
    await Test("special client zero-based rounds settle " + scenario.Name + " exactly once", async () =>
    {
        var (attacker, defender) = await RatingPair(false, false);
        // Put the attacker below this opponent so a win must exchange both ranks.
        (attacker.Arena!.SpecialRank, defender.Arena!.SpecialRank) = (defender.Arena!.SpecialRank, attacker.Arena!.SpecialRank);
        long attackRank = attacker.Arena.SpecialRank, defendRank = defender.Arena.SpecialRank;
        await Post<ResSpecialArenaStart>(attacker.ID, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = (long)defender.ID });
        var request = new ReqSpecialArenaResult { EnemyUsn = (long)defender.ID,
            Result = { scenario.Wins.Select((won, round) => new NetSpecialArenaRoundResult { Round = round, IsWin = won }) } };
        string nonce = Guid.NewGuid().ToString("N");
        var result = await Post<ResSpecialArenaResult>(attacker.ID, "arena/special/result", request, nonce);
        Check(result.Battle.User.After.Rank == (scenario.Won ? defendRank : attackRank) &&
            result.Battle.Enemy.After.Rank == (scenario.Won ? attackRank : defendRank), "wrong best-of-three rank settlement");
        Check(attacker.Arena.SpecialCount == 1 && attacker.Arena.Pending == null, "completed battle remained pending or charged again");
        var attackRecord = NetSpecialArenaRecordData.Parser.ParseFrom(attacker.Arena.SpecialRecords.Single());
        var defendRecord = NetSpecialArenaRecordData.Parser.ParseFrom(defender.Arena.SpecialRecords.Single());
        Check(attackRecord.IsWin == scenario.Won && defendRecord.IsWin != scenario.Won &&
            attackRecord.IsAttack && !defendRecord.IsAttack, "attack and defense records disagree with client outcome");
        string settled = JsonConvert.SerializeObject(State());
        Check(result.Equals(await Post<ResSpecialArenaResult>(attacker.ID, "arena/special/result", request, nonce)) &&
            settled == JsonConvert.SerializeObject(State()), "retried zero-based result settled twice");
    });
    await Test("special malformed round sequences preserve the pending battle and both ranks", async () =>
    {
        var (attacker, defender) = await RatingPair(false, false);
        await Post<ResSpecialArenaStart>(attacker.ID, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = (long)defender.ID });
        var before = JToken.FromObject(State());
        foreach (var ids in new[] { Array.Empty<int>(), new[] { 0 }, new[] { 0, 0 }, new[] { 0, 2 },
            new[] { -1, 0 }, new[] { 2, 3 }, new[] { 0, 1, 3 }, new[] { 0, 1, 2, 3 } })
        {
            await Status(attacker.ID, "arena/special/result", new ReqSpecialArenaResult { EnemyUsn = (long)defender.ID,
                Result = { ids.Select(round => new NetSpecialArenaRoundResult { Round = round, IsWin = true }) } }, 400);
            Check(JToken.DeepEquals(before, JToken.FromObject(State())), "invalid round sequence changed saved state");
        }
        await Status(attacker.ID, "arena/special/result", new ReqSpecialArenaResult { EnemyUsn = (long)defender.ID,
            Result = { new NetSpecialArenaRoundResult { Round = 0, IsWin = true }, new NetSpecialArenaRoundResult { Round = 1, IsWin = false } } }, 400);
        Check(JToken.DeepEquals(before, JToken.FromObject(State())), "unfinished tied match changed saved state");
    });
}
Console.WriteLine($"Arena: {passed} PASS, {failed} FAIL");
return failed == 0 ? 0 : 1;

async Task Test(string name, Func<Task> body) { try { await body(); passed++; Console.WriteLine("PASS: " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL: " + name + ": " + e.Message); } }
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
async Task<HttpResponseMessage> Send(ulong id, string route, IMessage request, string? nonce = null)
{
    using var msg = new HttpRequestMessage(HttpMethod.Post, route);
    if (id != 0) msg.Headers.Add("Test-User", id.ToString());
    nonce ??= Guid.NewGuid().ToString("N");
    if (nonce.Length != 0) msg.Headers.Add("Test-Nonce", nonce.PadRight(48, '0'));
    msg.Content = new ByteArrayContent(request.ToByteArray()); msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    return await client.SendAsync(msg);
}
async Task<T> Post<T>(ulong id, string route, IMessage request, string? nonce = null) where T : IMessage<T>, new()
{
    using var response = await Send(id, route, request, nonce);
    if (!response.IsSuccessStatusCode) throw new Exception(route + " HTTP " + (int)response.StatusCode + " " + (await response.Content.ReadAsStringAsync()).Substring(0, Math.Min(120, (int)(response.Content.Headers.ContentLength ?? 120))));
    return new MessageParser<T>(() => new()).ParseFrom(await response.Content.ReadAsByteArrayAsync());
}
async Task Status(ulong id, string route, IMessage request, int expected)
{
    using var r = await Send(id, route, request);
    Check((int)r.StatusCode == expected, route + " expected " + expected + " got " + (int)r.StatusCode);
}
async Task<long> Target(ulong id, bool special)
{
    if (special) return (await Post<ResGetSpecialArenaEnemyList>(id, "arena/special/getenemylist", new ReqGetSpecialArenaEnemyList())).List2.First().User.User.Usn;
    return (await Post<ResGetArenaEnemyList>(id, "arena/getenemylist", new ReqGetArenaEnemyList())).List2.First().User.User.Usn;
}
async Task<(User Attacker, User Defender)> RatingPair(bool attackerNpc, bool defenderNpc)
{
    var template = State().Users.Where(u => u.ManagedNpc != null).OrderByDescending(u => u.LastNormalStageCleared).First();
    User Add(bool npc)
    {
        var player = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(template))!;
        player.ID = ++ratingFixtureId; player.Arena = null;
        // Retain constructor currency defaults so rollback deserialization does not repair an artificial empty wallet.
        player.Currency = new User().Currency;
        if (!npc) player.ManagedNpc = null;
        else player.ManagedNpc!.Enabled = true;
        State().Users.Add(player);
        using var db = GameContext.CreateNew();
        db.Users.Add(new GameUser { ID = player.ID, Nickname = "Rating" + player.ID, LastAction = clock.Now.UtcDateTime });
        db.SaveChanges();
        return player;
    }
    var attacker = Add(attackerNpc); var defender = Add(defenderNpc);
    await Post<ResGetArena>(attacker.ID, "arena/get", new ReqGetArena());
    await Post<ResGetArena>(defender.ID, "arena/get", new ReqGetArena());
    return (attacker, defender);
}
sealed class ArenaClock : TimeProvider { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow() => Now; }
