using System.Net.Http.Headers;
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
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;
using EpinelPS.Utils;
using EpinelPS.LobbyServer.Soloraid;

const ulong self = 9701, other = 9702, third = 9703;
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
if (root == null) throw new Exception("Workspace not found");
if (args.Contains("--probe-live-readonly"))
{
    try
    {
    // Run before loading JsonDb/GameData: the probe never loads or saves a live account in-process.
    string liveRoot = Environment.GetEnvironmentVariable("SOLO_RAID_LIVE_ROOT") ?? root.FullName;
    var live = JObject.Parse(File.ReadAllText(Path.Combine(liveRoot, "Server and Server selector/db.json")));
    ulong id = (ulong)live["Users"]![0]!["ID"]!;
    byte[] key = Convert.FromBase64String((string)live["LauncherTokenKey"]!);
    using var handler = new HttpClientHandler { UseProxy = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
    using var client = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1/v1/"), Timeout = TimeSpan.FromSeconds(20) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(id, key));
    using var body = new ByteArrayContent(new ReqGetSoloRaidRanking().ToByteArray());
    body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    using var reply = await client.PostAsync("soloraid/getranking", body);
    reply.EnsureSuccessStatusCode();
    var ranking = ResGetSoloRaidRanking.Parser.ParseFrom(await reply.Content.ReadAsByteArrayAsync());
    var own = ranking.User ?? throw new InvalidOperationException("live ranking reply has no personal row");
    Check(own.User?.Usn == (long)id && own.User.UserTitleDisplayData != null,
        "live ranking reply still lacks the personal profile");
    Check(ranking.TotalUserCount == ranking.Rankings.Count && ranking.Rankings.All(r => r.Ranking > 0 && r.Damage > 0 && r.User != null),
        "live participant count or ranked profiles invalid");
    var deployedConfig = JsonConvert.DeserializeObject<GameConfigRoot>(File.ReadAllText(Path.Combine(liveRoot, "Server and Server selector/gameconfig.json")))!;
    using var periodBody = new ByteArrayContent(new ReqGetSoloRaidPeriod().ToByteArray());
    periodBody.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    using var periodReply = await client.PostAsync("soloraid/getperiod", periodBody);
    periodReply.EnsureSuccessStatusCode();
    var actualPeriod = ResGetSoloRaidPeriod.Parser.ParseFrom(await periodReply.Content.ReadAsByteArrayAsync()).Period;
    Check(actualPeriod.Equals(deployedConfig.SoloRaidSeason.ToNet()), "live solo raid period does not match the fixed configured season");
    Console.WriteLine($"PASS live authenticated solo raid ranking: participants={ranking.TotalUserCount}, personalRank={own.Ranking}, personalDamage={own.Damage}, profile={own.User!.Nickname}");
    Console.WriteLine($"PASS live fixed solo raid period: manager={deployedConfig.SoloRaidSeason.RaidId}, endUtc={deployedConfig.SoloRaidSeason.EndAt:O}");
    return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("FAIL live authenticated solo raid ranking: " + ex.Message);
        return 1;
    }
}

var data = new GameData(Directory.GetFiles(Path.Combine(root.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
int raidId = data.SoloRaidManagerTable.Keys.Max();
foreach (var manager in data.SoloRaidManagerTable.Values.OrderByDescending(m => m.Id).Take(2))
{
    var preset = data.SoloRaidPresetTable.Values.Single(p => p.PresetGroupId == manager.MonsterPreset && p.WaveOrder == 8);
    Console.WriteLine($"SoloRaidStatic manager={manager.Id}, preset={JsonConvert.SerializeObject(preset)}");
}
int tid = data.CharacterTable.Keys.First();
var shippedConfig = GameConfig.Root;
var configField = typeof(GameConfig).GetField("_root", BindingFlags.Static | BindingFlags.NonPublic)!;
// Battle regression fixtures remain playable when this test is run after the real season.
SetSeason(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
string save = Path.Combine(AppContext.BaseDirectory, "db.json");
string connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "solo-raid-ranking.db")}";
bool restarting = args.Contains("--verify-saved");
GameContext.InitializeOptions(connection, "sqlite");
if (!restarting)
{
    File.WriteAllText(save, JsonConvert.SerializeObject(Fixture()));
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted(); db.Database.EnsureCreated();
    db.Users.AddRange(Enumerable.Range(9701, 11).Select(id => new GameUser { ID = (ulong)id, Nickname = "Solo" + id }));
    db.SaveChanges();
}
var state = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
byte[] tokenKey = State().LauncherTokenKey;
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
int passed = 0, failed = 0;
if (restarting)
{
    await Test("completed best records and public profiles survive a fresh process", async () =>
    {
        var result = await Ranking(self);
        Check(result.TotalUserCount == 2 && result.Rankings[0].User.Usn == (long)other && result.User.Ranking == 2 &&
            result.User.Damage == 9_000_000_000 && result.User.User.Nickname == "Solo9701", "saved rankings changed after restart");
        Check(Player(self).SoloRaidData[raidId].SoloRaidLevels.Single().Logs.Count == 5, "saved best squads lost");
    });
}
else
{
    await Test("empty board returns an unranked personal profile instead of an empty protobuf", async () =>
    {
        var result = await Ranking(self);
        Check(result.TotalUserCount == 0 && result.Rankings.Count == 0 && result.User is { Ranking: 0, Damage: 0, User.Usn: (long)self },
            "empty leaderboard lacks the complete unranked user entry");
        Check(result.User.User.UserTitleDisplayData != null && result.BanResult == SoloRaidBanResult.Success, "profile defaults incomplete");
    });
    await Test("completed challenge results rank globally with 64-bit damage and stable ties", async () =>
    {
        Best(self, 8_000_000_000); Best(other, 9_000_000_000); Best(third, 8_000_000_000);
        var result = await Ranking(self);
        Check(result.Rankings.Select(r => r.User.Usn).SequenceEqual(new long[] { (long)other, (long)self, (long)third }), "incorrect score or tie order");
        Check(result.Rankings.Select(r => r.Ranking).SequenceEqual(new long[] { 1, 2, 3 }) && result.TotalUserCount == 3,
            "rank indices or participant count incorrect");
        Check(result.User.Ranking == 2 && result.User.Damage == 8_000_000_000, "personal score/rank missing or truncated");
    });
    await Test("normal, practice, unfinished, old-season and non-challenge stages never enter ranking", async () =>
    {
        Best(self, 20);
        Best(other, long.MaxValue, type: SoloRaidType.Normal);
        Best(third, long.MaxValue, type: SoloRaidType.Practice);
        Best(9704, long.MaxValue, open: true);
        Best(9705, long.MaxValue, season: raidId - 1);
        Best(9706, long.MaxValue, level: 7);
        Best(9707, 0); Best(9708, -1);
        Best(9709, long.MaxValue, clear: false);
        var result = await Ranking(self);
        Check(result.TotalUserCount == 1 && result.Rankings.Single().Damage == 20, "non-qualifying scores entered leaderboard");
        Check((await Ranking(other)).User.Damage == 0 && (await Ranking(other)).User.Ranking == 0, "normal damage became personal challenge damage");
    });
    await Test("legacy duplicate completed records contribute only the highest result per account", async () =>
    {
        Best(self, 100); Best(self, 500); Best(self, 200); Best(other, 300);
        var result = await Ranking(self);
        Check(result.TotalUserCount == 2 && result.Rankings.Count == 2 && result.User.Damage == 500 && result.User.Ranking == 1,
            "duplicate legacy rows changed participant count or selected a lower best");
    });
    await Test("rank entries contain SQLite names, appearance and union membership", async () =>
    {
        Best(self, 10);
        Player(self).ProfileIconId = 10016; Player(self).ProfileFrame = 11; Player(self).ProfileIconIsPrism = true;
        Player(self).Guild.guildId = 1;
        State().Guilds[1] = new SavedGuild { Data = new NetGuildData { Gsn = 1, Name = "SoloUnion", Grade = 1 } };
        var profile = (await Ranking(self)).User.User;
        Check(profile.Nickname == "Solo9701" && profile.Icon == 10016 && profile.Frame == 11 && profile.IconPrism &&
            profile.GuildName == "SoloUnion" && profile.Lv == 237 && profile.UserTitleDisplayData != null, "profile data missing");
    });
    await Test("banned accounts are excluded and the requesting account receives its ban result", async () =>
    {
        Best(self, 10); Best(other, 20); Player(other).IsBanned = true;
        var result = await Ranking(self);
        Check(result.TotalUserCount == 1 && result.User.Ranking == 1, "banned result contributes to rankings");
        var banned = await Ranking(other);
        Check(banned.BanResult == SoloRaidBanResult.Banned && banned.User.Ranking == 0 && banned.User.Damage == 0,
            "banned personal row appears ranked");
    });
    await Test("ranking reads neither reset old attempt counters nor mutate any saved account", async () =>
    {
        Best(self, 42); var raid = Player(self).SoloRaidData[raidId];
        raid.RaidOpenCount = 3; raid.TrialCount = 2; raid.LastDateDay = 20200101;
        Player(self).Guild.guildId = 1;
        State().Guilds[1] = new SavedGuild { Data = new NetGuildData
            { Gsn = 1, Name = "ReadOnlyUnion", Grade = 1, ActivityPointDaily = 321, ActivityPointRecent = 654 } };
        string before = JsonConvert.SerializeObject(State()); byte[] fileBefore = File.ReadAllBytes(save);
        await Ranking(self); await Ranking(self);
        Check(JsonConvert.SerializeObject(State()) == before && File.ReadAllBytes(save).SequenceEqual(fileBefore), "rank query mutated attempts or save data");
    });
    await Test("unranked players see other participants and their own zero-score row", async () =>
    {
        Best(other, 123);
        var result = await Ranking(self);
        Check(result.TotalUserCount == 1 && result.Rankings.Single().User.Usn == (long)other &&
            result.User.User.Usn == (long)self && result.User.Ranking == 0 && result.User.Damage == 0, "unranked user reply invalid");
        Check(result.Rankings.All(r => r.ReportBattleId == 0 && r.ReportBattleDate == null), "nonexistent battle report fabricated");
    });
    await Test("invalid authentication cannot fetch personal ranking data", async () =>
    {
        bool rejected = false;
        try { await Send<ResGetSoloRaidRanking>(0, "/soloraid/getranking", new ReqGetSoloRaidRanking()); }
        catch (UnauthorizedAccessException) { rejected = true; }
        Check(rejected, "ranking route accepted an unauthenticated request");
    });
    await Test("newly opened and partially played challenges do not replace a completed best", async () =>
    {
        Best(self, 9_000);
        await Open(self);
        await Damage(self, 1_000);
        var result = await Ranking(self);
        Check(result.User.Damage == 9_000, "unfinished run replaced best");
        Check(Player(self).SoloRaidData[raidId].SoloRaidLevels.Count == 2, "current run or existing best disappeared");
    });
    await Test("five-team lower and equal attempts preserve best score and original squad logs", async () =>
    {
        var best = Best(self, 9_000);
        string snapshot = JsonConvert.SerializeObject(best);
        foreach (long perTeam in new long[] { 100, 1_800 })
        {
            await Open(self);
            ResSetSoloRaidTrialDamage? reply = null;
            for (int i = 0; i < 5; i++) reply = await Damage(self, perTeam);
            Check(reply is { RaidJoinCount: 5, Status: SoloRaidStatus.Kill } && reply.Info.Damage == perTeam * 5,
                "current run settlement reports the wrong damage/status");
            var records = Player(self).SoloRaidData[raidId].SoloRaidLevels;
            Check(records.Count == 1 && JsonConvert.SerializeObject(records[0]) == snapshot, "lower/equal run destroyed the best result or its logs");
            Check((await Ranking(self)).User.Damage == 9_000, "leaderboard regressed after another attempt");
        }
    });
    await Test("a new five-team personal best updates both rankings and the solo raid overview", async () =>
    {
        Best(self, 100); Best(other, 4_000_000_000);
        await Open(self);
        for (int i = 0; i < 4; i++) await Damage(self, 1_000_000_000);
        Check((await Ranking(self)).User.Damage == 100, "incomplete high run entered leaderboard");
        await Damage(self, 1_000_000_000);
        var result = await Ranking(self);
        var overview = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
        Check(result.User.Damage == 5_000_000_000 && result.User.Ranking == 1 && overview.Info.TrialDamage == result.User.Damage,
            "overview and ranking disagree on the completed best");
        Check(Player(self).SoloRaidData[raidId].SoloRaidLevels.Single().Logs.Count == 5, "completed best squad logs missing");
    });
    await Test("explicitly closing a partial attempt commits its result without lowering the old best", async () =>
    {
        Best(self, 100);
        await Open(self); await Damage(self, 200);
        await Send<ResCloseSoloRaidTrial>(self, "/soloraid/trial/close", new ReqCloseSoloRaidTrial { RaidId = raidId, RaidLevel = 8 });
        Check((await Ranking(self)).User.Damage == 200, "closed result did not enter ranking");
        await Open(self); await Damage(self, 50);
        await Send<ResCloseSoloRaidTrial>(self, "/soloraid/trial/close", new ReqCloseSoloRaidTrial { RaidId = raidId, RaidLevel = 8 });
        Check((await Ranking(self)).User.Damage == 200, "lower closed result replaced best");
    });
    await Test("rank queries and battle completion remain consistent under concurrent requests", async () =>
    {
        Best(self, 100);
        await Open(self);
        var readings = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            var result = await Ranking(self);
            Check(result.TotalUserCount == 1 && result.User.Ranking == 1 && result.Rankings.Single().Damage == result.User.Damage,
                "ranking read observed a partially updated result");
        }));
        await Task.WhenAll(readings.Append(Task.Run(async () => { for (int i = 0; i < 5; i++) await Damage(self, 200); })));
        Check((await Ranking(self)).User.Damage == 1_000, "final completed result missing");
    });
    await Test("default season has the official fixed deadline and retains it after config reload", async () =>
    {
        var fixtureConfig = GameConfig.Root;
        try
        {
            configField.SetValue(null, JsonConvert.DeserializeObject<GameConfigRoot>("{}"));
            var period = (await Send<ResGetSoloRaidPeriod>(self, "/soloraid/getperiod", new ReqGetSoloRaidPeriod())).Period;
            Check(period.StartDate == new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc).Ticks &&
                  period.EndDate == new DateTime(2026, 11, 18, 20, 0, 0, DateTimeKind.Utc).Ticks,
                "season window is still based on today's date rather than the fixed official schedule");
            configField.SetValue(null, JsonConvert.DeserializeObject<GameConfigRoot>("{}"));
            Check(SoloRaidHelper.GetSoloRaidPeriod().Equals(period), "season dates changed after a fresh config load");
        }
        finally { configField.SetValue(null, fixtureConfig); }
    });
    await Test("a preloaded higher manager cannot silently select an unscheduled boss", async () =>
    {
        data.SoloRaidManagerTable.Add(int.MaxValue, new SoloRaidManagerRecord { Id = int.MaxValue });
        try
        {
            var info = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
            Check(info.Info.SoloRaidManagerTid == raidId, "highest static manager replaced the explicitly scheduled boss");
        }
        finally { data.SoloRaidManagerTable.Remove(int.MaxValue); }
    });
    await Test("future and expired seasons reject every battle mutation while preserving standings", async () =>
    {
        foreach (bool expired in new[] { false, true })
        {
            Reset(); Best(self, 123_456); Best(self, 999, open: true);
            var fixtureConfig = GameConfig.Root;
            try
            {
                var now = DateTimeOffset.UtcNow;
                SetSeason(now.AddDays(expired ? -2 : 1), now.AddDays(expired ? -1 : 2));
                string snapshot = JsonConvert.SerializeObject(State().Users);
                await Rejected<ResOpenSoloRaid>("/soloraid/open", new ReqOpenSoloRaid { RaidId = raidId, RaidLevel = 1 });
                await Rejected<ResOpenSoloRaidTrial>("/soloraid/trial/open", new ReqOpenSoloRaidTrial { RaidLevel = 8 });
                await Rejected<ResOpenSoloRaidPractice>("/soloraid/practice/open", new ReqOpenSoloRaidPractice { RaidLevel = 8 });
                await Rejected<ResEnterSoloRaid>("/soloraid/enter", new ReqEnterSoloRaid { RaidId = raidId, RaidLevel = 1 });
                await Rejected<ResEnterSoloRaidTrial>("/soloraid/trial/enter", new ReqEnterSoloRaidTrial { RaidId = raidId, RaidLevel = 8 });
                await Rejected<ResSetSoloRaidDamage>("/soloraid/setdamage", new ReqSetSoloRaidDamage { RaidLevel = 1, Damage = long.MaxValue });
                await Rejected<ResSetSoloRaidTrialDamage>("/soloraid/trial/setdamage", new ReqSetSoloRaidTrialDamage { RaidLevel = 8, Damage = long.MaxValue });
                await Rejected<ResSetSoloRaidPracticeDamage>("/soloraid/practice/setdamage", new ReqSetSoloRaidPracticeDamage { RaidLevel = 8, Damage = long.MaxValue });
                await Rejected<ResCloseSoloRaid>("/soloraid/close", new ReqCloseSoloRaid { RaidId = raidId, RaidLevel = 1 });
                await Rejected<ResCloseSoloRaidTrial>("/soloraid/trial/close", new ReqCloseSoloRaidTrial { RaidId = raidId, RaidLevel = 8 });
                await Rejected<ResCloseSoloRaidPractice>("/soloraid/practice/close", new ReqCloseSoloRaidPractice { RaidId = raidId, RaidLevel = 8 });
                await Rejected<ResFastBattleSoloRaid>("/soloraid/fastbattle", new ReqFastBattleSoloRaid { RaidId = raidId, RaidLevel = 1, ClearCount = 3 });
                Check(JsonConvert.SerializeObject(State().Users) == snapshot, "rejected battle changed saved scores, teams, attempts or rewards");
                Check((await Ranking(self)).User.Damage == 123_456, "final standings became unreadable");
                var info = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
                Check(info.Info.TrialDamage == 123_456 && info.Info.Period.EndDate == now.AddDays(expired ? -1 : 2).UtcTicks,
                    "overview lost the score or the fixed configured deadline");
            }
            finally { configField.SetValue(null, fixtureConfig); }
        }
    });
    await Test("an active season rejects explicit requests for another raid", async () =>
    {
        int oldRaid = data.SoloRaidManagerTable.Keys.Where(id => id != raidId).First();
        string snapshot = JsonConvert.SerializeObject(State().Users);
        await Rejected<ResOpenSoloRaid>("/soloraid/open", new ReqOpenSoloRaid { RaidId = oldRaid, RaidLevel = 1 });
        await Rejected<ResFastBattleSoloRaid>("/soloraid/fastbattle", new ReqFastBattleSoloRaid { RaidId = oldRaid, RaidLevel = 1, ClearCount = 3 });
        Check(JsonConvert.SerializeObject(State().Users) == snapshot, "out-of-season raid granted rewards or changed attempts");
    });
    await Test("UTC start is inclusive and end is exclusive across midnight and timezone offsets", () =>
    {
        var season = GameConfig.Root.SoloRaidSeason;
        foreach (var instant in new[] { season.StartAt.AddTicks(-1), season.EndAt, season.EndAt.AddTicks(1), season.EndAt.AddDays(1) })
            Check(SoloRaidHelper.GetPeriodResult(raidId, instant) == SoloRaidPeriodResult.Failure, "closed boundary accepted play");
        var period = SoloRaidHelper.GetSoloRaidPeriod();
        foreach (var instant in new[] { season.StartAt, season.StartAt.AddHours(8), season.EndAt.AddTicks(-1) })
        {
            Check(SoloRaidHelper.GetPeriodResult(raidId, instant) == SoloRaidPeriodResult.Success &&
                  SoloRaidHelper.GetPeriodResult(raidId, instant.ToOffset(TimeSpan.FromHours(8))) == SoloRaidPeriodResult.Success,
                "active UTC instant was treated differently across dates/timezones");
            Check(SoloRaidHelper.GetSoloRaidPeriod().Equals(period), "checking another day moved the deadline");
        }
        return Task.CompletedTask;
    });
    await Test("invalid or missing season configuration fails closed", () =>
    {
        var config = GameConfig.Root;
        var season = config.SoloRaidSeason;
        try
        {
            config.SoloRaidSeason = new() { StartAt = season.EndAt, EndAt = season.StartAt };
            Check(SoloRaidHelper.GetPeriodResult() == SoloRaidPeriodResult.Failure, "reversed dates accepted play");
            config.SoloRaidSeason = null!;
            Check(SoloRaidHelper.GetPeriodResult() == SoloRaidPeriodResult.Failure, "null season accepted play");
        }
        finally { config.SoloRaidSeason = season; }
        return Task.CompletedTask;
    });
    // The last fixture is saved by real damage-submission handlers, then verified by a fresh process.
    Reset(); Best(other, 10_000_000_000); await Open(self);
    for (int i = 0; i < 5; i++) await Damage(self, 1_800_000_000);
}
Console.WriteLine($"SoloRaidRanking: {passed} PASS, {failed} FAIL");
return failed == 0 ? 0 : 1;

CoreInfo Fixture() => new() { DbVersion = 5, BondRewardLevelsMigrated = true,
    Users = Enumerable.Range(9701, 11).Select(id => new User { ID = (ulong)id, LastReset = DateTime.UtcNow,
        LastWeeklyReset = DateTime.UtcNow, userPointData = new() { UserLevel = 237 } }).ToList() };
CoreInfo State() => (CoreInfo)state.GetValue(null)!;
User Player(ulong id) => State().Users.Single(u => u.ID == id);
void Reset() { var fixture = Fixture(); fixture.LauncherTokenKey = tokenKey; state.SetValue(null, fixture); }
SoloRaidLevelData Best(ulong id, long damage, SoloRaidType type = SoloRaidType.Trial, bool open = false,
    int? season = null, int level = 8, bool clear = true)
{
    int key = season ?? raidId;
    if (!Player(id).SoloRaidData.TryGetValue(key, out var raid))
        Player(id).SoloRaidData[key] = raid = new SoloRaidInfo { RaidId = key, LastDateDay = Player(id).GetDateDay() };
    var result = new SoloRaidLevelData { RaidLevel = level, TotalDamage = damage, IsOpen = open, IsClear = clear,
        Type = type, RaidJoinCount = 5, Status = SoloRaidStatus.Kill,
        Logs = [new SoloRaidLogData { Damage = damage, Kill = true, Team = [new TeamCharacterData { Slot = 1, Tid = tid, Csn = 1, Lv = 237 }] }] };
    raid.SoloRaidLevels.Add(result);
    return result;
}
Task<ResGetSoloRaidRanking> Ranking(ulong id) => Send<ResGetSoloRaidRanking>(id, "/soloraid/getranking", new ReqGetSoloRaidRanking());
Task<ResOpenSoloRaidTrial> Open(ulong id) => Send<ResOpenSoloRaidTrial>(id, "/soloraid/trial/open", new ReqOpenSoloRaidTrial { RaidLevel = 8 });
Task<ResSetSoloRaidTrialDamage> Damage(ulong id, long damage) => Send<ResSetSoloRaidTrialDamage>(id, "/soloraid/trial/setdamage",
    new ReqSetSoloRaidTrialDamage { RaidLevel = 8, Damage = damage, AntiCheatBattleData = new()
        { Characters = { new NetAntiCheatCharacter { Tid = tid, Csn = 1, Slot = 1, CharacterSpec = new() { Level = 237, Combat = 1234 } } } } });
async Task<T> Send<T>(ulong id, string route, IMessage request) where T : IMessage<T>, new()
{
    using var scope = services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Request.Path = "/v1" + route;
    ctx.Request.Body = new MemoryStream(request.ToByteArray());
    ctx.Response.Body = new MemoryStream();
    if (id != 0) ctx.Request.Headers.Authorization = "Bearer " + Token(id, tokenKey);
    await LobbyHandler.DispatchSingle(ctx);
    Check(ctx.Response.StatusCode == 200 && ctx.Response.ContentType == "application/octet-stream+protobuf", "invalid response transport");
    T reply = new(); reply.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray()); return reply;
}
async Task Test(string name, Func<Task> action)
{
    try { if (!restarting) Reset(); await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
static string Token(ulong id, byte[] key) => new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
    .WithKey(key, Encryption.SymmetricKey).AddClaim("userId", id).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

void SetSeason(DateTimeOffset start, DateTimeOffset end)
{
    var config = JObject.FromObject(shippedConfig);
    config["SoloRaidSeason"] = JObject.FromObject(new { RaidId = raidId, VisibleAt = start,
        StartAt = start, EndAt = end, SettleAt = end, DisableAt = end.AddDays(7) });
    configField.SetValue(null, config.ToObject<GameConfigRoot>());
}

async Task Rejected<T>(string route, IMessage request) where T : IMessage<T>, new()
{
    var response = await Send<T>(self, route, request);
    var result = (SoloRaidPeriodResult)typeof(T).GetProperty("PeriodResult")!.GetValue(response)!;
    Check(result == SoloRaidPeriodResult.Failure, route + " accepted play outside the configured season");
}
