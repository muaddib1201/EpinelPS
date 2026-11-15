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
    // Read only static data/cache in this probe process; do not initialize JsonDb or a live player database.
    typeof(GameConfig).GetField("_root", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, deployedConfig);
    string liveCache = Path.Combine(liveRoot, "Server and Server selector/cache");
    string staticPath = Path.GetFullPath(Path.Combine(liveCache, new Uri(deployedConfig.StaticDataMpk.Url).GetComponents(UriComponents.Path, UriFormat.Unescaped).TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
    Check(staticPath.StartsWith(Path.GetFullPath(liveCache) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "static-data path escaped the live cache");
    var liveData = new GameData(staticPath);
    typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, liveData);
    await GameDataInitializer.InitializeGameData(new Progress<double>());
    var liveCatalog = new SoloRaidSeasonCatalog(() => liveData, Path.Combine(liveCache, "solo-raid-nikkehub.json"));
    bool hasCache = liveCatalog.LoadCache();
    if (deployedConfig.SoloRaidAutoSync) Check(hasCache, "automatic solo raid synchronization has no durable source cache");
    var expectedSeason = deployedConfig.SoloRaidAutoSync ? liveCatalog.Resolve(deployedConfig.SoloRaidSeason, DateTimeOffset.UtcNow)! : deployedConfig.SoloRaidSeason;
    using var periodBody = new ByteArrayContent(new ReqGetSoloRaidPeriod().ToByteArray());
    periodBody.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    using var periodReply = await client.PostAsync("soloraid/getperiod", periodBody);
    periodReply.EnsureSuccessStatusCode();
    var actualPeriod = ResGetSoloRaidPeriod.Parser.ParseFrom(await periodReply.Content.ReadAsByteArrayAsync()).Period;
    Check(actualPeriod.Equals(expectedSeason.ToNet()), "live solo raid period does not match the resolved fixed season");
    Console.WriteLine($"PASS live authenticated solo raid ranking: participants={ranking.TotalUserCount}, personalRank={own.Ranking}, personalDamage={own.Damage}, profile={own.User!.Nickname}");
    Console.WriteLine($"PASS live fixed solo raid period: manager={expectedSeason.RaidId}, endUtc={expectedSeason.EndAt:O}");
    if (deployedConfig.SoloRaidAutoSync) Console.WriteLine($"PASS live automatic solo raid source cache: {JsonConvert.SerializeObject(liveCatalog.GetStatus(deployedConfig.SoloRaidSeason, DateTimeOffset.UtcNow))}");
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
            NetScore(result.User.Damage) == 9_000_000_000 && result.User.User.Nickname == "Solo9701", "saved rankings changed after restart");
        Check(Player(self).SoloRaidData[raidId].SoloRaidLevels.Single().Logs.Count == 5, "saved best squads lost");
    });
}
else
{
    await Test("season 43 sends base plus observed five-team net score to overview and both ranking rows", async () =>
    {
        int oldRaid = raidId;
        var oldConfig = GameConfig.Root;
        try
        {
            raidId = 1000044;
            SetSeason(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            const long baseline = 1_130_781_186, net = 2_007_459_930, wire = 3_138_241_116;
            Check(NormalBase(raidId) == baseline, "season 43 static normal HP no longer matches the observed base");
            long[] teams = [1_749_538_166, 44_716_660, 133_407_166, 40_266_510, 39_531_428];
            await Open(self);
            ResSetSoloRaidTrialDamage? settlement = null;
            foreach (long damage in teams) settlement = await Damage(self, damage);
            Check(settlement!.Info.Damage == net, "battle settlement did not retain net damage");
            var saved = Player(self).SoloRaidData[raidId].SoloRaidLevels.Single();
            Check(saved.TotalDamage == net && saved.Logs.Select(l => l.Damage).SequenceEqual(teams), "stored score or battle logs are not net damage");
            var overview = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
            var ranking = await Ranking(self);
            Check(overview.Info.TrialDamage == wire && ranking.Rankings.Single().Damage == wire && ranking.User.Damage == wire,
                $"expected {wire}; overview={overview.Info.TrialDamage}, list={ranking.Rankings.Single().Damage}, personal={ranking.User.Damage}");
            Check(wire - baseline == net, "client-visible decoded score differs from net battle total");
        }
        finally { raidId = oldRaid; configField.SetValue(null, oldConfig); }
    });
    await Test("low positive score and duplicate completed best agree in both endpoints", async () =>
    {
        long baseline = NormalBase(raidId);
        Best(self, 1); Best(self, 2);
        var result = await Ranking(self);
        var overview = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
        Check(result.User.Damage == baseline + 2 && overview.Info.TrialDamage == baseline + 2,
            $"expected {baseline + 2}, ranking={result.User.Damage}, overview={overview.Info.TrialDamage}");
    });
    await Test("maximum representable net score encodes without overflow", async () =>
    {
        long baseline = NormalBase(raidId);
        Best(self, long.MaxValue - baseline);
        var ranking = await Ranking(self);
        var overview = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
        Check(ranking.User.Damage == long.MaxValue && overview.Info.TrialDamage == long.MaxValue, "maximum score failed to encode");
    });
    await Test("another season uses its own HP and saturates without changing raw order or battle logs", async () =>
    {
        int originalRaid = raidId;
        var originalConfig = GameConfig.Root;
        SoloRaidPresetRecord? changedPreset = null;
        int originalLevel = 0;
        try
        {
            long season43 = NormalBase(1000044);
            var candidates = new List<(int Id, long Hp)>();
            foreach (var candidate in data.SoloRaidManagerTable.Values.Where(m => m.Id != 1000044))
            {
                try { candidates.Add((candidate.Id, NormalBase(candidate.Id))); }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { }
            }
            Check(candidates.Count > 0, "no other complete season in isolated static data");
            var selected = candidates.FirstOrDefault(c => c.Hp != season43);
            if (selected == default)
            {
                selected = candidates[0];
                var manager = data.SoloRaidManagerTable[selected.Id];
                var stages = data.SoloRaidPresetTable.Values.Where(p => p.PresetGroupId == manager.MonsterPreset && p.WaveOrder is >= 1 and <= 7).OrderBy(p => p.WaveOrder).ToArray();
                changedPreset = stages[0]; originalLevel = changedPreset.MonsterStageLv;
                changedPreset.MonsterStageLv = stages[1].MonsterStageLv;
                selected = (selected.Id, NormalBase(selected.Id));
                Check(selected.Hp != season43, "fixture adjustment failed to create a different normal HP base");
            }
            raidId = selected.Id;
            SetSeason(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            Console.WriteLine($"SeasonBase43={season43}, otherSeason={raidId}, otherBase={selected.Hp}, fixtureModified={changedPreset != null}, completeOtherSeasons={candidates.Count}, distinctBases={candidates.Select(c => c.Hp).Append(season43).Distinct().Count()}");
            Best(self, long.MaxValue - selected.Hp + 1);
            Best(other, long.MaxValue);
            var ranking = await Ranking(self);
            var overview = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
            Check(ranking.Rankings.Select(r => r.User.Usn).SequenceEqual(new long[] { (long)other, (long)self }), "saturation changed raw ranking order");
            Check(ranking.Rankings.All(r => r.Damage == long.MaxValue) && overview.Info.TrialDamage == long.MaxValue, "scores above the limit did not saturate");
            var logs = await RankerSquad(self, (long)other, raidId);
            Check(logs.Logs.Single().Damage == long.MaxValue && Player(other).SoloRaidData[raidId].SoloRaidLevels.Single().TotalDamage == long.MaxValue, "saturation changed raw battle logs or saved score");
        }
        finally
        {
            if (changedPreset != null) changedPreset.MonsterStageLv = originalLevel;
            raidId = originalRaid;
            configField.SetValue(null, originalConfig);
        }
    });
    await Test("missing normal stage fails overview and ranking instead of returning a partial score", async () =>
    {
        Best(self, 2_007_459_930);
        var manager = data.SoloRaidManagerTable[raidId];
        var stage = data.SoloRaidPresetTable.Values.Single(p => p.PresetGroupId == manager.MonsterPreset && p.WaveOrder == 4);
        Check(data.SoloRaidPresetTable.Remove(stage.Id), "failed to remove isolated fixture stage");
        try
        {
            var overview = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
            Check(overview.PeriodResult == SoloRaidPeriodResult.Failure, $"missing normal stage returned {overview.PeriodResult} with score {overview.Info?.TrialDamage}");
        }
        finally { data.SoloRaidPresetTable.Add(stage.Id, stage); }
    });
    await Test("missing normal stage returns HTTP 503 for ranking", async () =>
    {
        Best(self, 2_007_459_930);
        var manager = data.SoloRaidManagerTable[raidId];
        var stage = data.SoloRaidPresetTable.Values.Single(p => p.PresetGroupId == manager.MonsterPreset && p.WaveOrder == 4);
        Check(data.SoloRaidPresetTable.Remove(stage.Id), "failed to remove isolated fixture stage");
        try { await Send<ResGetSoloRaidRanking>(self, "/soloraid/getranking", new ReqGetSoloRaidRanking(), expectedStatus: 503); }
        finally { data.SoloRaidPresetTable.Add(stage.Id, stage); }
    });
    await Test("empty board returns an unranked personal profile instead of an empty protobuf", async () =>
    {
        var result = await Ranking(self);
        Check(result.TotalUserCount == 0 && result.Rankings.Count == 0 && result.User is { Ranking: 0, Damage: 0, User.Usn: (long)self },
            "empty leaderboard lacks the complete unranked user entry");
        Check(result.User.User.UserTitleDisplayData != null && result.BanResult == SoloRaidBanResult.Success, "profile defaults incomplete");
        var overview = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
        Check(overview.Info.TrialDamage == 0, "missing challenge score was encoded as a positive score");
    });
    await Test("completed challenge results rank globally with 64-bit damage and stable ties", async () =>
    {
        Best(self, 8_000_000_000); Best(other, 9_000_000_000); Best(third, 8_000_000_000);
        var result = await Ranking(self);
        Check(result.Rankings.Select(r => r.User.Usn).SequenceEqual(new long[] { (long)other, (long)self, (long)third }), "incorrect score or tie order");
        Check(result.Rankings.Select(r => r.Ranking).SequenceEqual(new long[] { 1, 2, 3 }) && result.TotalUserCount == 3,
            "rank indices or participant count incorrect");
        Check(result.User.Ranking == 2 && NetScore(result.User.Damage) == 8_000_000_000, "personal score/rank missing or truncated");
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
        Check(result.TotalUserCount == 1 && NetScore(result.Rankings.Single().Damage) == 20, "non-qualifying scores entered leaderboard");
        Check((await Ranking(other)).User.Damage == 0 && (await Ranking(other)).User.Ranking == 0, "normal damage became personal challenge damage");
    });
    await Test("legacy duplicate completed records contribute only the highest result per account", async () =>
    {
        Best(self, 100); Best(self, 500); Best(self, 200); Best(other, 300);
        var result = await Ranking(self);
        Check(result.TotalUserCount == 2 && result.Rankings.Count == 2 && NetScore(result.User.Damage) == 500 && result.User.Ranking == 1,
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
    await Test("ranker squad details return the five saved teams of the target's ranked run in battle order", async () =>
    {
        Best(self, 123);
        long[] damages = [2_200_000_000, 70, 40, 20, 10];
        var best = Best(other, damages.Sum());
        best.Logs = damages.Select((damage, i) => new SoloRaidLogData
        {
            Damage = damage,
            Team = [new TeamCharacterData { Slot = 1, Csn = 100 + i, Tid = tid, Lv = 400, Combat = 3000 + i }]
        }).ToList();
        Best(other, 400);
        Best(other, long.MaxValue, open: true);
        Best(other, long.MaxValue, type: SoloRaidType.Practice);
        Player(other).SoloRaidData[raidId].LastDateDay = 20200101;
        string before = JsonConvert.SerializeObject(State().Users);
        var ranking = await Ranking(self);
        var row = ranking.Rankings.Single(r => r.User.Usn == (long)other);
        foreach (var requestedRaid in new[] { raidId, 0 })
        {
            var details = await RankerSquad(self, row.User.Usn, requestedRaid);
            Check(details.Logs.Select(log => log.Damage).SequenceEqual(damages),
                $"expected five ordered best-run squad damages, received {details.Logs.Count} logs");
            Check(details.Logs.Sum(log => log.Damage) == NetScore(row.Damage), "squad damage did not add up to the ranked result");
            var members = details.Logs.Select(log => log.Team.Single()).ToArray();
            Check(members.Select(m => m.Combat).SequenceEqual(new[] { 3000, 3001, 3002, 3003, 3004 }) &&
                members.All(m => m.Slot == 1 && m.Tid == tid && m.Lv == 400), "saved squad snapshots were lost or reordered");
        }
        Check((await RankerSquad(self, (long)self, raidId)).Logs.Single().Damage == 123, "personal detail returned another player's squad");
        Check(JsonConvert.SerializeObject(State().Users) == before, "reading ranker squad details changed a saved account");
    });
    await Test("ranker squad details never substitute a different user, season or unfinished challenge", async () =>
    {
        Best(self, 100);
        Best(other, 300, open: true);
        Best(other, 700, season: raidId - 1);
        Check((await RankerSquad(self, (long)other, raidId)).Logs.Count == 0, "an open trial or another season supplied a squad");
        Check((await RankerSquad(self, (long)self, int.MaxValue)).Logs.Count == 0, "an unknown season fell back to the current record");
        Check((await RankerSquad(self, long.MaxValue, raidId)).Logs.Count == 0, "unknown target returned a squad");
        Best(other, 800);
        Player(other).IsBanned = true;
        Check((await RankerSquad(self, (long)other, raidId)).Logs.Count == 0, "a banned target exposed a ranked squad");
        var banned = await RankerSquad(other, (long)self, raidId);
        Check(banned.BanResult == SoloRaidBanResult.Banned && banned.Logs.Count == 0, "ranker squad response lost the viewer's ban result");
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
    await Test("lobby badge reports normal entries used after a sweep independently of challenge attempts", async () =>
    {
        Best(self, 10, type: SoloRaidType.Normal, level: 7);
        var sweep = await Send<ResFastBattleSoloRaid>(self, "/soloraid/fastbattle",
            new ReqFastBattleSoloRaid { RaidId = raidId, RaidLevel = 7, ClearCount = 3 });
        Check(sweep.PeriodResult == SoloRaidPeriodResult.Success && sweep.RaidOpenCount == 3, "three normal sweeps did not settle");
        int used = await BadgeNormalPlayCount(self);
        Check(used == 3, $"lobby badge returned {used} normal entries used after three sweeps");

        Best(other, 20);
        var otherRaid = Player(other).SoloRaidData[raidId];
        otherRaid.RaidOpenCount = 1;
        otherRaid.TrialCount = 3;
        Check(await BadgeNormalPlayCount(other) == 1, "challenge attempts or another player affected the badge");
    });
    await Test("lobby badge ignores stale and missing records without changing raid progress", async () =>
    {
        Best(self, 42);
        var yesterday = Player(self).SoloRaidData[raidId];
        yesterday.LastDateDay = 20200101;
        yesterday.RaidOpenCount = 3;
        yesterday.TrialCount = 2;
        int previousRaid = data.SoloRaidManagerTable.Keys.First(id => id != raidId);
        Best(other, 50, season: previousRaid);
        Player(other).SoloRaidData[previousRaid].RaidOpenCount = 3;
        string before = JsonConvert.SerializeObject(State().Users);
        foreach (ulong id in new[] { self, other, third })
            Check(await BadgeNormalPlayCount(id) == 0, "a stale or missing current-season record gave a nonzero badge count");
        Check(JsonConvert.SerializeObject(State().Users) == before, "reading the badge created or reset raid progress");
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
        Check(NetScore(result.User.Damage) == 9_000, "unfinished run replaced best");
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
            Check(NetScore((await Ranking(self)).User.Damage) == 9_000, "leaderboard regressed after another attempt");
        }
    });
    await Test("a new five-team personal best updates both rankings and the solo raid overview", async () =>
    {
        Best(self, 100); Best(other, 4_000_000_000);
        await Open(self);
        for (int i = 0; i < 4; i++) await Damage(self, 1_000_000_000);
        Check(NetScore((await Ranking(self)).User.Damage) == 100, "incomplete high run entered leaderboard");
        await Damage(self, 1_000_000_000);
        var result = await Ranking(self);
        var overview = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
        Check(NetScore(result.User.Damage) == 5_000_000_000 && result.User.Ranking == 1 && overview.Info.TrialDamage == result.User.Damage,
            "overview and ranking disagree on the completed best");
        Check(Player(self).SoloRaidData[raidId].SoloRaidLevels.Single().Logs.Count == 5, "completed best squad logs missing");
    });
    await Test("explicitly closing a partial attempt commits its result without lowering the old best", async () =>
    {
        Best(self, 100);
        await Open(self); await Damage(self, 200);
        await Send<ResCloseSoloRaidTrial>(self, "/soloraid/trial/close", new ReqCloseSoloRaidTrial { RaidId = raidId, RaidLevel = 8 });
        Check(NetScore((await Ranking(self)).User.Damage) == 200, "closed result did not enter ranking");
        await Open(self); await Damage(self, 50);
        await Send<ResCloseSoloRaidTrial>(self, "/soloraid/trial/close", new ReqCloseSoloRaidTrial { RaidId = raidId, RaidLevel = 8 });
        Check(NetScore((await Ranking(self)).User.Damage) == 200, "lower closed result replaced best");
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
        Check(NetScore((await Ranking(self)).User.Damage) == 1_000, "final completed result missing");
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
                Player(self).SoloRaidData[raidId].RaidOpenCount = 2;
                string snapshot = JsonConvert.SerializeObject(State().Users);
                Check(await BadgeNormalPlayCount(self) == 0, "an inactive season supplied normal attempts to the lobby badge");
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
                Check(NetScore((await Ranking(self)).User.Damage) == 123_456, "final standings became unreadable");
                var info = await Send<ResGetSoloRaidInfo>(self, "/soloraid/get", new ReqGetSoloRaidInfo());
                Check(NetScore(info.Info.TrialDamage) == 123_456 && info.Info.Period.EndDate == now.AddDays(expired ? -1 : 2).UtcTicks,
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
    await Test("late damage without a current-season open run cannot create a fresh season score", async () =>
    {
        Best(self, 123_456, season: data.SoloRaidManagerTable.Keys.First(id => id != raidId));
        string snapshot = JsonConvert.SerializeObject(State().Users);
        var rejected = await Damage(self, 999_999);
        Check(rejected.PeriodResult == SoloRaidPeriodResult.Failure && JsonConvert.SerializeObject(State().Users) == snapshot,
            "an old or missing run created a score in the current season");
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
long NormalBase(int managerId)
{
    var manager = data.SoloRaidManagerTable[managerId];
    long total = 0;
    for (int level = 1; level <= 7; level++)
    {
        var preset = data.SoloRaidPresetTable.Values.Single(p => p.PresetGroupId == manager.MonsterPreset && p.WaveOrder == level);
        var monsterId = data.WaveIntercept001Table[preset.Wave].TargetList![0];
        int group = data.MonsterTable[monsterId].StatenhanceId;
        total = checked(total + data.MonsterStatEnhanceTable.Values.Where(x => x.GroupId == group && x.Lv == preset.MonsterStageLv).Sum(x => (long)x.LevelHp));
    }
    return total;
}
long NetScore(long wire) => wire == 0 ? 0 : checked(wire - NormalBase(raidId));
Task<ResGetSoloRaidRanking> Ranking(ulong id) => Send<ResGetSoloRaidRanking>(id, "/soloraid/getranking", new ReqGetSoloRaidRanking());
Task<ResGetSoloRaidRankerSquad> RankerSquad(ulong viewer, long target, int requestedRaid) =>
    Send<ResGetSoloRaidRankerSquad>(viewer, "/soloraid/getrankersquad",
        new ReqGetSoloRaidRankerSquad { TargetUsn = target, RaidId = requestedRaid });
async Task<int> BadgeNormalPlayCount(ulong id) =>
    (await Send<ResPermanentContentBadgeData>(id, "/badge/permanentcontent", new ReqPermanentContentBadgeData())).TodaySoloRaidNormalPlayCount;
Task<ResOpenSoloRaidTrial> Open(ulong id) => Send<ResOpenSoloRaidTrial>(id, "/soloraid/trial/open", new ReqOpenSoloRaidTrial { RaidLevel = 8 });
Task<ResSetSoloRaidTrialDamage> Damage(ulong id, long damage) => Send<ResSetSoloRaidTrialDamage>(id, "/soloraid/trial/setdamage",
    new ReqSetSoloRaidTrialDamage { RaidLevel = 8, Damage = damage, AntiCheatBattleData = new()
        { Characters = { new NetAntiCheatCharacter { Tid = tid, Csn = 1, Slot = 1, CharacterSpec = new() { Level = 237, Combat = 1234 } } } } });
async Task<T> Send<T>(ulong id, string route, IMessage request, int expectedStatus = 200) where T : IMessage<T>, new()
{
    using var scope = services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Request.Path = "/v1" + route;
    ctx.Request.Body = new MemoryStream(request.ToByteArray());
    ctx.Response.Body = new MemoryStream();
    if (id != 0) ctx.Request.Headers.Authorization = "Bearer " + Token(id, tokenKey);
    await LobbyHandler.DispatchSingle(ctx);
    Check(ctx.Response.StatusCode == expectedStatus, $"{route} expected HTTP {expectedStatus}, got {ctx.Response.StatusCode}");
    if (expectedStatus != 200) return new T();
    Check(ctx.Response.ContentType == "application/octet-stream+protobuf", "invalid response transport");
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
