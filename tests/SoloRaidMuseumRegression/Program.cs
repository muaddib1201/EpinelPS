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
using Paseto;
using Paseto.Builder;

const ulong self = 9701, other = 9702;
const long reaperDamage = 4_940_621_589, reaperHp = 1_157_048_016;
bool reload = args.Contains("--verify-museum-reload");
var root = new DirectoryInfo(Environment.GetEnvironmentVariable("NIKKE_WORKSPACE_ROOT") ?? AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
if (root == null) throw new Exception("Session workspace not found");
var data = new GameData(Directory.GetFiles(Path.Combine(root.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
if (args.Contains("--probe-live-readonly")) return await MuseumLiveProbe.Run();
var fixture = new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true,
    Users = [new User { ID = self }, new User { ID = other }] };
if (!reload) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "db.json"), JsonConvert.SerializeObject(fixture));
string connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "museum.db")}";
GameContext.InitializeOptions(connection, "sqlite");
if (!reload)
using (var db = GameContext.CreateNew())
{
    db.Database.EnsureDeleted(); db.Database.EnsureCreated();
    db.Users.AddRange(new GameUser { ID = self, Nickname = "MuseumSelf" }, new GameUser { ID = other, Nickname = "MuseumOther" });
    db.SaveChanges();
}
var stateProperty = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
CoreInfo State() => (CoreInfo)stateProperty.GetValue(null)!;
User Player() => State().Users.Single(u => u.ID == self);
byte[] tokenKey = State().LauncherTokenKey;
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
int passed = 0, failed = 0;
var stages = data.MuseumStageTable.Values.OrderBy(s => s.Id).ToArray();

if (reload)
{
    var persisted = await History(other, 3, 301);
    var winner = persisted.RankerRankingList.Single(r => r.Ranking == 1);
    Check(persisted.TotalUserCount == 1 && winner.PastUserData.Usn == (long)self &&
        winner.Damage == reaperDamage + reaperHp, "persisted local ranking differs");
    Check((await Squad(other, 301, (long)self)).SquadList.Sum(s => s.Damage) == reaperDamage, "persisted details differ");
    return 0;
}

await Test("empty stages retain mission slots and the viewer without invented competitors", async () =>
{
    foreach (var stage in stages)
    {
        var board = await History(self, stage.GroupId, stage.Id);
        Check(board.TotalUserCount == 0 && board.RankerRankingList.All(Vacant), "phantom entrant");
        Check(board.UserRanking is { Ranking: 0, Damage: 0 } && board.UserRanking.CurrentUserData.Nickname == "MuseumSelf",
            "an unplayed stage lost its personal row");
        var missions = await Send<ResGetSoloRaidMuseumMission>(self, "/soloraidmuseum/get/mission",
            new ReqGetSoloRaidMuseumMission { StageId = stage.Id });
        var slots = missions.ChallengeMissionDataList.Select(m => data.MuseumMissionTable[m.MissionId])
            .Where(m => m.ConditionType == MuseumMissionConditionType.GetTotalDamageRanker)
            .Select(m => m.ConditionValue).OrderDescending();
        Check(board.RankerRankingList.Select(r => r.Ranking).SequenceEqual(slots) && slots.Any(), "rank mission lookup missing");
    }
    Check(State().Users.Single(u => u.ID == other).SoloRaidMuseumData.Count == 0, "reading created stages on another account");
});

await Test("Reaper ranks the fifth team settlement and both ranking rows decode to the saved best", async () =>
{
    await Send<ResOpenSoloRaidMuseumChallenge>(self, "/soloraidmuseum/challenge/open",
        new ReqOpenSoloRaidMuseumChallenge { StageId = 301 });
    for (int team = 1; team <= 4; team++) await Submit(team, 1_000_000_000);
    Check((await History(other, 3, 301)).TotalUserCount == 0, "four-team run ranked");
    await Submit(5, reaperDamage - 4_000_000_000);
    var board = await History(self, 3, 301);
    var publicRow = board.RankerRankingList.Single(r => r.Ranking == 1);
    var privateRow = board.UserRanking;
    Check(board.TotalUserCount == 1 && publicRow.PastUserData.Nickname == "MuseumSelf" &&
        publicRow.CurrentUserData.Usn == (long)self && privateRow.Ranking == 1, "local winner missing");
    Check(board.RankerRankingList.Where(r => r.Ranking != 1).All(Vacant), "unreached tiers populated");
    Check(publicRow.Damage == privateRow.Damage && privateRow.Damage - reaperHp == reaperDamage, "Reaper HP mismatch");
    var own = await Send<ResGetSoloRaidMuseumMyPastSquad>(self, "/soloraidmuseum/get/mypastsquad",
        new ReqGetSoloRaidMuseumMyPastSquad { StageId = 301 });
    var publicLogs = (await Squad(other, 301, (long)self)).SquadList;
    Check(publicLogs.SequenceEqual(own.SquadList) && publicLogs.Count == 5 && publicLogs.Sum(s => s.Damage) == reaperDamage,
        "saved details have an offset or belong to another player");
    var overview = await Send<ResGetSoloRaidMuseumData>(self, "/soloraidmuseum/get/data", new ReqGetSoloRaidMuseumData());
    Check(overview.GroupInfoList.SelectMany(g => g.ChallengeStageInfoList).Single(s => s.StageId == 301).BestDamage == reaperDamage,
        "display conversion changed the stored best");
    await Send<ResOpenSoloRaidMuseumChallenge>(self, "/soloraidmuseum/challenge/open",
        new ReqOpenSoloRaidMuseumChallenge { StageId = 301 });
    await Submit(1, 8_000_000_000);
    Check((await History(self, 3, 301)).UserRanking.Damage == privateRow.Damage &&
        (await Squad(other, 301, (long)self)).SquadList.Sum(s => s.Damage) == reaperDamage, "unfinished replay replaced the best");
});

await Test("vacant rank slots do not award missions or count the ranking HP offset as progress", async () =>
{
    var stage = stages.First(s => s.GroupId == 2);
    var definitions = data.MuseumMissionTable.Values.Where(m =>
        m.StageId == stage.Id && m.ModeType == MuseumStageModeType.Challenge).ToArray();
    var rankMissions = definitions.Where(m => m.ConditionType == MuseumMissionConditionType.GetTotalDamageRanker)
        .OrderBy(m => m.Order).ToArray();
    long threshold = definitions.Where(m => m.ConditionType == MuseumMissionConditionType.GetTotalDamage)
        .OrderBy(m => m.ConditionValue).TakeLast(rankMissions.Length).First().ConditionValue;
    var owner = State().Users.Single(u => u.ID == other);
    owner.SoloRaidMuseumData[stage.Id] = new() { StageId = stage.Id, Challenge = new() { BestDamage = threshold - 1 } };
    var board = await History(other, stage.GroupId, stage.Id);
    Check(Vacant(board.RankerRankingList.Single(r => r.Ranking == rankMissions[0].ConditionValue)), "expected a vacant tier");
    var missions = await Send<ResGetSoloRaidMuseumMission>(other, "/soloraidmuseum/get/mission",
        new ReqGetSoloRaidMuseumMission { StageId = stage.Id });
    var mission = missions.ChallengeMissionDataList.Single(m => m.MissionId == rankMissions[0].Id);
    Check(mission.Progress == threshold - 1 && !mission.IsReceived, "wire HP leaked into mission progress");
    var claim = await Send<ResAcquireSoloRaidMuseumChallengeMission>(other, "/soloraidmuseum/challenge/acquire/mission",
        new ReqAcquireSoloRaidMuseumChallengeMission { MissionIdList = { mission.MissionId } });
    Check(claim.MissionDataList.Count == 0 && !owner.SoloRaidMuseumData[stage.Id].ReceivedChallengeMissions.Contains(mission.MissionId),
        "a vacant tier granted an unearned reward");
});

await Test("real local profiles fill reached ranks and ties have stable positions", async () =>
{
    using var db = GameContext.CreateNew();
    for (int i = 0; i < 50; i++)
    {
        ulong id = (ulong)(10000 + i);
        var account = new User { ID = id };
        if (i == 4) account.ManagedNpc = new() { Name = "NoSignal" };
        long damage = 2_000_000_000L - Math.Max(0, i - 1);
        account.SoloRaidMuseumData[101] = new() { StageId = 101, Challenge = new() { BestDamage = damage } };
        State().Users.Add(account);
        db.Users.Add(new GameUser { ID = id, Nickname = i == 4 ? "NoSignal" : $"Raider-{i + 1}" });
    }
    db.SaveChanges();
    var board = await History(10001, 1, 101);
    Check(board.TotalUserCount == 50 && board.UserRanking.Ranking == 2, "tied local scores reordered");
    long hp = data.MuseumCommonStageHpGroupTable.Values.Where(h => h.Group == data.MuseumStageTable[101].SoloRaidStageHpGroup).Sum(h => h.HpValue);
    foreach (var row in board.RankerRankingList)
    {
        long id = 9999 + row.Ranking;
        var user = State().Users.Single(u => (long)u.ID == id);
        Check(row.PastUserData.Usn == id && row.CurrentUserData.Usn == id &&
            row.PastUserData.Nickname == db.Users.Find(user.ID)!.Nickname &&
            row.Damage - hp == user.SoloRaidMuseumData[101].Challenge.BestDamage, "tier is not its actual local finisher");
    }
    Check((await History(self, 1, 101)).UserRanking is { Ranking: 0, Damage: 0 }, "a spectator got an entrant's score");
});

await Test("unknown, banned, unplayed, and no-limit-only participants have no public results", async () =>
{
    const int stage = 102;
    var orphan = new User { ID = 22000, SoloRaidMuseumData = { [stage] = new() { Challenge = new() { BestDamage = 5000 } } } };
    State().Users.Add(orphan);
    var banned = State().Users.Single(u => u.ID == 10001);
    banned.IsBanned = true;
    banned.SoloRaidMuseumData[stage] = new() { Challenge = new() { BestDamage = 6000 } };
    Player().SoloRaidMuseumData[stage] = new() { NoLimit = new() { BestDamage = 9000 } };
    Check((await History(other, 1, stage)).TotalUserCount == 0, "ineligible score published");
    foreach (var target in new long[] { 0, -1, (long)self, (long)banned.ID, (long)orphan.ID, 990000000 })
        Check((await Squad(other, stage, target)).SquadList.Count == 0, "ineligible target exposed a team");
    Check((await Squad(other, 999999, (long)self)).SquadList.Count == 0 && (await Squad(other, 301, 10004)).SquadList.Count == 0,
        "cross-stage or unknown detail exposed");
});

await Test("legacy records keep their real score and only the saved characters in squad details", async () =>
{
    var stage = Player().SoloRaidMuseumData[102];
    stage.Challenge.BestDamage = 719_000_000;
    stage.Challenge.Logs = [new() { TeamNumber = 1, Damage = 719_000_000 }];
    Player().Characters = [new CharacterModel { Csn = 123, Tid = data.CharacterTable.Keys.First(), Level = 400 }];
    stage.Teams = [new NetTeamData { TeamNumber = 1, Slots = { new NetTeamSlot { Slot = 1, Value = 123 } } }];
    Check((await History(self, 1, 102)).UserRanking.Ranking == 1, "old save lost its best");
    var detail = (await Squad(other, 102, (long)self)).SquadList.Single();
    Check(detail.Team.Count == 0 && detail.Damage == 719_000_000, "legacy record borrowed a current team");
    stage.Challenge.Logs[0].Team.Add(new TeamCharacterData { Slot = 1, Tid = 10101, Lv = 205, Combat = 12345 });
    var saved = (await Squad(other, 102, (long)self)).SquadList.Single().Team.Single();
    Check(saved.Tid == 10101 && saved.Lv == 205 && saved.Combat == 12345, "saved team omitted");
    stage.Challenge.BestDamage = long.MaxValue;
    Check((await History(self, 1, 102)).UserRanking.Damage == long.MaxValue, "wire score overflowed");
});

await Test("a fresh process reloads the settled run while a new attempt is incomplete", async () =>
{
    var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--verify-museum-reload");
    using var child = System.Diagnostics.Process.Start(start)!;
    await child.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
    Check(child.ExitCode == 0, "reloaded board or team details changed");
});
Console.WriteLine($"SoloRaidMuseum: {passed} PASS, {failed} FAIL");
return failed == 0 ? 0 : 1;

static bool Vacant(NetSoloRaidMuseumRankerRankingData row) =>
    row.Damage == 0 && row.PastUserData == null && row.CurrentUserData == null;

Task<ResSetSoloRaidMuseumChallengeDamage> Submit(int team, long damage) =>
    Send<ResSetSoloRaidMuseumChallengeDamage>(self, "/soloraidmuseum/challenge/set/damage",
        new ReqSetSoloRaidMuseumChallengeDamage { StageId = 301, BattleData = new NetSoloRaidMuseumBattleData { Team = team, Damage = damage } });

async Task<NetSoloRaidMuseumStagePastRanking> History(ulong id, int group, int stage) =>
    (await Send<ResGetSoloRaidMuseumGroupData>(id, "/soloraidmuseum/get/groupdata", new ReqGetSoloRaidMuseumGroupData { GroupId = group })).PastRankingList.Single(s => s.StageId == stage);
Task<ResGetSoloRaidMuseumRankerSquad> Squad(ulong id, int stage, long target) =>
    Send<ResGetSoloRaidMuseumRankerSquad>(id, "/soloraidmuseum/get/rankersquad", new ReqGetSoloRaidMuseumRankerSquad { StageId = stage, TargetUsn = target });
async Task<T> Send<T>(ulong id, string route, IMessage request) where T : IMessage<T>, new()
{
    using var scope = services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Request.Path = "/v1" + route; ctx.Request.Body = new MemoryStream(request.ToByteArray()); ctx.Response.Body = new MemoryStream();
    ctx.Request.Headers.Authorization = "Bearer " + new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
        .WithKey(tokenKey, Encryption.SymmetricKey).AddClaim("userId", id).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
    await LobbyHandler.DispatchSingle(ctx);
    Check(ctx.Response.StatusCode == 200 && ctx.Response.ContentType == "application/octet-stream+protobuf", "invalid response transport");
    T reply = new(); reply.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray()); return reply;
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
