using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.LobbyServer.Team;
using EpinelPS.LobbyServer.Stage;
using EpinelPS.LobbyServer.Tower;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
var pack = Path.GetFullPath(Path.Combine(sourceRoot,
    "../../Server and Server selector/cache/prdenv/153-cb882d4b30/staticdata/data/qa-261029-10b/578481/mpk/StaticData.pack"));
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
var progress = new Progress<double>();
if (args.Contains("--inspect"))
{
    Console.WriteLine(JsonConvert.SerializeObject(await data.LoadZip<SupportContentsRecord>("SupportContentsTable.json", progress)));
    Console.WriteLine(JsonConvert.SerializeObject(await data.LoadZip<SupportCharacterGroupRecord>("SupportCharacterGroupTable.json", progress)));
    return;
}
await GameDataInitializer.InitializeGameData(progress);

var dbType = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var dbProperty = dbType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!;
var user = JsonConvert.DeserializeObject<User>("""
    {"ID": 90001, "ResetableData": {"SupportCharacterUsedCounts": {"1": {"10820401": 1}}}}
    """)!;
user.LastReset = user.LastWeeklyReset = DateTime.UtcNow;
dbProperty.SetValue(null, new CoreInfo { Users = [user] });

var response = await Send<ResListSupportCharacterUsedCount>(new ListSupportCharacterCount(),
    new ReqListSupportCharacterUsedCount { TeamTypeList = { 1 } });
var count = response.SupportCharacterUsedCountList.SingleOrDefault(x => x.TeamType == 1 && x.SupportCharacterId == 10820401);
if (count?.UsedSupportCharacterCount != 1)
    throw new Exception($"FAIL: a saved successful use must be returned as used=1 (remaining 2/3); got {count?.UsedSupportCharacterCount.ToString() ?? "no entry"}");
Console.WriteLine("PASS: saved support use is returned by the real query handler");

GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "support-regression.sqlite")}", "sqlite");
using (var context = GameContext.CreateNew())
{
    context.Database.EnsureDeleted();
    context.Database.EnsureCreated();
}
int passed = 1;
List<string> failures = [];
await Check("campaign victory charges all participating supports once and persists them", async () =>
{
    user = NewUser();
    await EnterCampaign();
    Equal(0, await Used(1, 10820401));
    await ClearCampaign(1);
    foreach (int id in new[] { 10820401, 23210401, 31910401 }) Equal(1, await Used(1, id));
    dbType.GetMethod("Reload", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
    user = ((CoreInfo)dbProperty.GetValue(null)!).Users.Single();
    Equal(1, await Used(1, 10820401));
});
await Check("defeat and repeated successful settlement do not consume extra uses", async () =>
{
    user = NewUser();
    await EnterCampaign();
    await ClearCampaign(0);
    Equal(0, await Used(1, 10820401));
    await EnterCampaign();
    await ClearCampaign(1);
    await ClearCampaign(1);
    Equal(1, await Used(1, 10820401));
});
await Check("third success reaches zero remaining and a fourth entry is rejected", async () =>
{
    user = NewUser();
    for (int i = 0; i < 3; i++) { await EnterCampaign(); await ClearCampaign(1); }
    Equal(3, await Used(1, 10820401));
    await Reject(() => EnterCampaign());
    Equal(3, await Used(1, 10820401));
});
await Check("settlement uses the entry snapshot even after switching the saved squad", async () =>
{
    user = NewUser();
    await EnterCampaign();
    user.UserTeams[1].Teams[0].Slots.Clear();
    await ClearCampaign(1);
    Equal(1, await Used(1, 10820401));
});
await Check("the requested team is used and owned-character slots never consume support uses", async () =>
{
    user = NewUser();
    await EnterCampaign(2);
    await ClearCampaign(1, 2);
    Equal(0, await Used(1, 10820401));
    Equal(1, await Used(1, 31910401));
    user.UserTeams[1].Teams[0].Slots.Clear();
    user.UserTeams[1].Teams[0].Slots.Add(new NetTeamSlot { Slot = 1, Value = 10820401, ValueType = 0 });
    await EnterCampaign();
    await ClearCampaign(1);
    Equal(0, await Used(1, 10820401));
});
await Check("real tower settlement charges its own counter independently from campaign", async () =>
{
    user = NewUser();
    await EnterCampaign();
    await ClearCampaign(1);
    await Send<ResEnterTower>(new EnterTower(), new ReqEnterTower { TowerId = 10001, TeamNumber = 1 });
    await Send<ResClearTower>(new ClearTower(), new ReqClearTower { TowerId = 10001, TeamNumber = 1, BattleResult = 1 });
    Equal(1, await Used(1, 10820401));
    Equal(1, await Used(5, 10820401));
    Equal(0, await Used(7, 10820401));
});
await Check("tower types map to the correct separate team counters", () =>
{
    user = NewUser();
    foreach (var (towerId, teamType) in new[] { (10001, 5), (20001, 6), (30001, 7), (40001, 8) })
    {
        Equal(teamType, (int)SupportCharacterHelper.GetTowerTeamType(towerId));
        SupportCharacterHelper.BeginBattle(user, (TeamType)teamType, towerId, 1);
        SupportCharacterHelper.FinishBattle(user, (TeamType)teamType, towerId, 1, 1);
        Equal(1, SupportCharacterHelper.GetUsedCounts(user, [teamType]).Single(x => x.SupportCharacterId == 10820401).UsedSupportCharacterCount);
    }
    return Task.CompletedTask;
});
await Check("daily rollover clears counters and permits using an exhausted support again", async () =>
{
    user = NewUser();
    for (int i = 0; i < 3; i++) { await EnterCampaign(); await ClearCampaign(1); }
    user.LastReset = DateTime.UtcNow.AddDays(-2);
    Equal(0, await Used(1, 10820401));
    await EnterCampaign();
    await ClearCampaign(1);
    Equal(1, await Used(1, 10820401));
});
await Check("a battle spanning daily rollover consumes one use on the settlement day", async () =>
{
    user = NewUser();
    await EnterCampaign();
    await ClearCampaign(1);
    await EnterCampaign();
    user.LastReset = DateTime.UtcNow.AddDays(-2);
    await ClearCampaign(1);
    Equal(1, await Used(1, 10820401));
});
await Check("unlimited event and archive contents never accumulate limited uses", () =>
{
    user = NewUser();
    foreach (int teamType in new[] { 20, 21, 23 })
    {
        for (int i = 0; i < 4; i++)
        {
            SupportCharacterHelper.BeginBattle(user, (TeamType)teamType, 123, 1);
            SupportCharacterHelper.FinishBattle(user, (TeamType)teamType, 123, 1, 1);
        }
        Equal(0, SupportCharacterHelper.GetUsedCounts(user, [teamType]).Count);
    }
    return Task.CompletedTask;
});
await Check("mismatched settlements cannot consume or remove another battle's snapshot", async () =>
{
    user = NewUser();
    await EnterCampaign();
    SupportCharacterHelper.FinishBattle(user, TeamType.Default, 999, 1, 1);
    SupportCharacterHelper.FinishBattle(user, TeamType.Default, 6003007, 2, 1);
    Equal(0, await Used(1, 10820401));
    await ClearCampaign(1);
    Equal(1, await Used(1, 10820401));
});
await Check("query respects requested contents and duplicate filters", async () =>
{
    user = NewUser();
    await EnterCampaign();
    await ClearCampaign(1);
    var filtered = await Send<ResListSupportCharacterUsedCount>(new ListSupportCharacterCount(),
        new ReqListSupportCharacterUsedCount { TeamTypeList = { 5 } });
    Equal(0, filtered.SupportCharacterUsedCountList.Count);
    var repeated = await Send<ResListSupportCharacterUsedCount>(new ListSupportCharacterCount(),
        new ReqListSupportCharacterUsedCount { TeamTypeList = { 1, 1 } });
    Equal(3, repeated.SupportCharacterUsedCountList.Count);
});
await Check("old saves without support fields deserialize with zero usage", async () =>
{
    user = JsonConvert.DeserializeObject<User>("{\"ID\":90001}")!;
    user.LastReset = user.LastWeeklyReset = DateTime.UtcNow;
    dbProperty.SetValue(null, new CoreInfo { Users = [user] });
    Equal(0, await Used(1, 10820401));
});
await Check("tower settlement refreshes yesterday's exhausted daily quota before awarding a clear", async () =>
{
    user = NewUser();
    await Send<ResEnterTower>(new EnterTower(), new ReqEnterTower { TowerId = 20001, TeamNumber = 1 });
    user.ResetableData.TowerCount[CorporationTowerType.ELYSION] = 3;
    user.LastReset = DateTime.UtcNow.AddDays(-2);
    await Send<ResClearTower>(new ClearTower(), new ReqClearTower { TowerId = 20001, TeamNumber = 1, BattleResult = 1 });
    Equal(1, await Used(6, 10820401));
    Equal(1, user.ResetableData.TowerCount[CorporationTowerType.ELYSION]);
});
await Check("campaign quick battle consumes support uses once and respects exhaustion", async () =>
{
    user = NewUser();
    await Send<ResFastClearCampaignStage>(new FastClear(), new ReqFastClearCampaignStage { CampaignStageId = 6003007 });
    Equal(1, await Used(1, 10820401));
    await Send<ResFastClearCampaignStage>(new FastClear(), new ReqFastClearCampaignStage { CampaignStageId = 6003007 });
    Equal(1, await Used(1, 10820401));
    for (int i = 0; i < 2; i++) { await EnterCampaign(); await ClearCampaign(1); }
    await Reject(() => Send<ResFastClearCampaignStage>(new FastClear(), new ReqFastClearCampaignStage { CampaignStageId = 6003006 }));
    Equal(3, await Used(1, 10820401));
});
await Check("quick battle charges the last selected squad rather than assuming squad one", async () =>
{
    user = NewUser();
    user.UserTeams[1].LastContentsTeamNumber = 2;
    await Send<ResFastClearCampaignStage>(new FastClear(), new ReqFastClearCampaignStage { CampaignStageId = 6003007 });
    Equal(0, await Used(1, 10820401));
    Equal(0, await Used(1, 23210401));
    Equal(1, await Used(1, 31910401));
});
await Check("quick battle honors an explicit squad instead of a different saved squad", async () =>
{
    user = NewUser();
    await Send<ResFastClearCampaignStage>(new FastClear(), new ReqFastClearCampaignStage { CampaignStageId = 6003007, TeamNumber = 2 });
    Equal(0, await Used(1, 10820401));
    Equal(1, await Used(1, 31910401));
    user = NewUser();
    user.UserTeams[1].LastContentsTeamNumber = 2;
    await Send<ResFastClearCampaignStage>(new FastClear(), new ReqFastClearCampaignStage { CampaignStageId = 6003007, TeamNumber = 1 });
    Equal(1, await Used(1, 10820401));
});
await Check("entry snapshots and consumed settlements survive server state reload", async () =>
{
    user = NewUser();
    await EnterCampaign();
    Reload();
    await ClearCampaign(1);
    Reload();
    await ClearCampaign(1);
    Equal(1, await Used(1, 10820401));
});
await Check("administrative stage completion does not consume selected support uses", async () =>
{
    user = NewUser();
    ClearStage.CompleteStage(user, 6003007);
    Equal(0, await Used(1, 10820401));
});
if (failures.Count > 0) throw new Exception($"FAIL: {string.Join("; ", failures)}");
Console.WriteLine($"PASS: {passed} regression scenarios");

User NewUser()
{
    var next = new User { ID = 90001, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow };
    using var context = GameContext.CreateNew();
    context.Triggers.ExecuteDelete();
    if (!context.Users.Any(x => x.ID == next.ID))
    {
        context.Users.Add(new GameUser { ID = next.ID });
        context.SaveChanges();
    }
    foreach (int type in new[] { 1, 5, 6, 7, 8, 20, 21, 23 })
        next.UserTeams[type] = new NetUserTeamData
        {
            Type = type, LastContentsTeamNumber = 1,
            Teams =
            {
                new NetTeamData
                {
                    TeamNumber = 1,
                    Slots =
                    {
                        new NetTeamSlot { Slot = 1, Value = 10820401, ValueType = 1 },
                        new NetTeamSlot { Slot = 2, Value = 23210401, ValueType = 1 },
                        new NetTeamSlot { Slot = 3, Value = 31910401, ValueType = 1 }
                    }
                },
                new NetTeamData
                {
                    TeamNumber = 2,
                    Slots = { new NetTeamSlot { Slot = 1, Value = 31910401, ValueType = 1 } }
                }
            }
        };
    dbProperty.SetValue(null, new CoreInfo { Users = [next] });
    return next;
}

Task<ResEnterStage> EnterCampaign(int team = 1) => Send<ResEnterStage>(new EnterStage(),
    new ReqEnterStage { StageId = 6003007, TeamNumber = team });
Task<ResClearStage> ClearCampaign(int result, int team = 1) => Send<ResClearStage>(new ClearStage(),
    new ReqClearStage { StageId = 6003007, TeamNumber = team, BattleResult = result });
async Task<int> Used(int teamType, int characterId)
{
    var result = await Send<ResListSupportCharacterUsedCount>(new ListSupportCharacterCount(),
        new ReqListSupportCharacterUsedCount { TeamTypeList = { teamType } });
    return result.SupportCharacterUsedCountList.SingleOrDefault(x => x.SupportCharacterId == characterId)?.UsedSupportCharacterCount ?? 0;
}
async Task Check(string name, Func<Task> check)
{
    try
    {
        await check();
        passed++;
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception e)
    {
        failures.Add($"{name}: {e.Message}");
        Console.WriteLine($"FAIL: {name}: {e.Message}");
    }
}
void Equal(int expected, int actual)
{
    if (expected != actual) throw new Exception($"Expected {expected}, got {actual}");
}
void Reload()
{
    dbType.GetMethod("Reload", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
    user = ((CoreInfo)dbProperty.GetValue(null)!).Users.Single();
}
async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (InvalidOperationException e) when (e.Message.Contains("no daily uses left")) { return; }
    throw new Exception("Expected exhausted support entry to be rejected");
}

async Task<T> Send<T>(LobbyMessage handler, IMessage request) where T : IMessage<T>, new()
{
    using var services = new ServiceCollection()
        .AddSingleton(new GameContext(new DbContextOptionsBuilder<GameContext>().Options))
        .BuildServiceProvider();
    var context = new DefaultHttpContext { RequestServices = services };
    context.Items["UserID"] = 90001UL;
    context.Request.Body = new MemoryStream(request.ToByteArray());
    context.Response.Body = new MemoryStream();
    await handler.HandleAsync(context);
    var result = new T();
    result.MergeFrom(((MemoryStream)context.Response.Body).ToArray());
    return result;
}
