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
var root = new DirectoryInfo(Environment.GetEnvironmentVariable("NIKKE_WORKSPACE_ROOT") ?? AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
if (root == null) throw new Exception("Session workspace not found");
var data = new GameData(Directory.GetFiles(Path.Combine(root.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var fixture = new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true,
    Users = [new User { ID = self }, new User { ID = other }] };
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "db.json"), JsonConvert.SerializeObject(fixture));
string connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "museum.db")}";
GameContext.InitializeOptions(connection, "sqlite");
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

await Test("historical profiles are independent NPC archives for every stage", async () =>
{
    foreach (var stage in stages)
    {
        var history = await History(self, stage.GroupId, stage.Id);
        Check(history.TotalUserCount == 50, "complete benchmark participant count missing");
        Check(history.RankerRankingList.Select(r => r.Ranking).SequenceEqual(new long[] { 50, 30, 15, 5, 1 }), "rank mission tiers missing");
        Check(history.RankerRankingList.Select(r => r.PastUserData.Usn).Distinct().Count() == 5, "historical identities reused");
        foreach (var row in history.RankerRankingList)
            Check(row.PastUserData.Usn != (long)self && row.PastUserData.Usn != (long)other &&
                row.PastUserData.Nickname.StartsWith("NPC") && row.CurrentUserData.Usn == row.PastUserData.Usn &&
                row.CurrentUserData.Nickname == row.PastUserData.Nickname && row.PastUserData.UserTitleDisplayData != null,
                "viewer data leaked into museum history or NPC identity incomplete");
    }
});
await Test("historical damage and all five squads agree on every stage and rank", async () =>
{
    foreach (var stage in stages)
    {
        var history = await History(self, stage.GroupId, stage.Id);
        long hp = data.MuseumCommonStageHpGroupTable.Values.Where(h => h.Group == stage.SoloRaidStageHpGroup).Sum(h => h.HpValue);
        var previous = 0L;
        foreach (var row in history.RankerRankingList)
        {
            var squads = await Squad(self, stage.Id, row.PastUserData.Usn);
            Check(squads.SquadList.Count == 5, "historical five-team detail missing");
            Check(squads.SquadList.Select(s => s.TeamNumber).SequenceEqual(new[] { 1, 2, 3, 4, 5 }), "squad order missing");
            Check(squads.SquadList.All(s => s.Damage > 0 && s.Team.Count == 5), "incomplete squad or damage");
            Check(squads.SquadList.Sum(s => s.Damage) == row.Damage - hp, "rank and squad damage disagree");
            Check(row.Damage > previous, "rank damage is not increasing"); previous = row.Damage;
            var characters = squads.SquadList.SelectMany(s => s.Team).ToArray();
            Check(characters.All(c => data.CharacterTable.ContainsKey(c.Tid) && c.Lv > 0 && c.Combat > 0), "invalid historical character details");
            Check(characters.Select(c => data.CharacterTable[c.Tid].NameCode).Distinct().Count() == 25, "character reused across five teams");
            Check(data.UserIconTable.ContainsKey(row.PastUserData.Icon), "invalid archive portrait");
            Check(squads.SquadList.All(s => new[] { BurstStep.Step1, BurstStep.Step2, BurstStep.Step3 }.All(b => s.Team.Any(c => data.CharacterTable[c.Tid].UseBurstSkill == b))), "historical squad lacks a burst phase");
        }
    }
});
await Test("archive does not depend on the viewer and mutations cannot change later requests", async () =>
{
    var a = await History(self, 1, 101); var b = await History(other, 1, 101);
    Check(a.RankerRankingList.Select(x => x.ToByteString()).SequenceEqual(b.RankerRankingList.Select(x => x.ToByteString())), "history changes with viewer");
    var original = a.RankerRankingList[0].ToByteString();
    a.RankerRankingList[0].PastUserData.Nickname = "mutated";
    Player().ProfileIconId = 1; Player().TitleId = 99;
    Check((await History(self, 1, 101)).RankerRankingList[0].ToByteString().Equals(original), "history references a mutable viewer profile");
    var squads = await Squad(self, 101, b.RankerRankingList[0].PastUserData.Usn);
    var snapshot = squads.ToByteString(); squads.SquadList[0].Team[0].Lv = 1;
    Check((await Squad(other, 101, b.RankerRankingList[0].PastUserData.Usn)).ToByteString().Equals(snapshot), "squad response can mutate the archive");
});
await Test("unknown targets never expose player squads", async () =>
{
    Check((await Squad(self, 101, (long)self)).SquadList.Count == 0, "current user substituted for an archive target");
    Check((await Squad(self, 999999, 1)).SquadList.Count == 0 && (await Squad(self, 101, 0)).SquadList.Count == 0, "unknown archive target not rejected");
    var row = (await History(self, 1, 101)).RankerRankingList[0];
    Check((await Squad(self, 102, row.PastUserData.Usn)).SquadList.Count == 0, "another season's target leaked");
});
await Test("personal best rank and details use only the account's saved completed result", async () =>
{
    Player().SoloRaidMuseumData[101] = new SoloRaidMuseumStageData { StageId = 101,
        Challenge = new SoloRaidMuseumModeData { BestDamage = 719_000_000, TotalDamage = 9_000_000_000,
            StageJoinCount = 1, IsInProgress = true,
            Logs = [new SoloRaidMuseumLogData { TeamNumber = 1, Damage = 719_000_000,
                Team = [new TeamCharacterData { Slot = 1, Tid = 10101, Lv = 200, Combat = 12345 }] }],
            CurrentLogs = [new SoloRaidMuseumLogData { TeamNumber = 1, Damage = 9_000_000_000 }] } };
    var history = await History(self, 1, 101);
    Check(history.UserRanking.Damage == 719_000_000 && history.UserRanking.Ranking == 15, "personal best rank not calculated from archive tiers");
    Check((await History(other, 1, 101)).UserRanking.Damage == 0, "another viewer's score leaked");
    var own = await Send<ResGetSoloRaidMuseumMyPastSquad>(self, "/soloraidmuseum/get/mypastsquad", new ReqGetSoloRaidMuseumMyPastSquad { StageId = 101 });
    Check(own.SquadList.Single().Damage == 719_000_000 && own.SquadList[0].Team.Single().Combat == 12345, "in-progress squad replaced saved best");
    foreach (var (damage, rank) in new (long, long)[] { (0, 0), (1, 51), (488_000_000, 50), (800_000_000, 8), (971_000_000, 1), (9_000_000_000, 1) })
    {
        Player().SoloRaidMuseumData[101].Challenge.BestDamage = damage;
        var row = (await History(self, 1, 101)).UserRanking;
        Check(row.Damage == damage && row.Ranking == rank, $"personal rank boundary failed at {damage}");
    }
});
await Test("legacy personal history never borrows today's roster when its snapshot is missing", async () =>
{
    Player().Characters = [new CharacterModel { Csn = 123, Tid = data.CharacterTable.Keys.First(), Level = 400 }];
    Player().SoloRaidMuseumData[101].Teams = [new NetTeamData { TeamNumber = 1,
        Slots = { new NetTeamSlot { Slot = 1, Value = 123 } } }];
    Player().SoloRaidMuseumData[101].Challenge.Logs[0].Team.Clear();
    var own = await Send<ResGetSoloRaidMuseumMyPastSquad>(self, "/soloraidmuseum/get/mypastsquad", new ReqGetSoloRaidMuseumMyPastSquad { StageId = 101 });
    Check(own.SquadList.Single().Team.Count == 0, "legacy personal history borrowed the current roster");
});
Console.WriteLine($"SoloRaidMuseum: {passed} PASS, {failed} FAIL");
return failed == 0 ? 0 : 1;

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
