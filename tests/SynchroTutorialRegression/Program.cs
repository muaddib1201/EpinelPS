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

const int group = 2500, partial = 2500041, terminal = 2500141;
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
foreach (var step in await data.LoadZip<ContentsTutorialRecord>("ContentsTutorialTable.json", new Progress<double>()))
    data.TutorialTable.Add(step.Id, step);
var last = data.TutorialTable[terminal];
Check(last.GroupId == group && last.NextId == 0 && last.SaveTutorial, "expected the shipped synchro tutorial terminal");

var amplified = Fixture(1, 5);
amplified.SynchroDeviceUpgraded = true;
amplified.SynchroDeviceLevel = 237;
amplified.SynchroSlots.AddRange(amplified.Characters.Select((c, i) => new SynchroSlot { Slot = i + 1, CharacterSerialNumber = c.Csn }));
var ordinary = Fixture(2, 4);
var ready = Fixture(3, 5);
ready.ClearedTutorialDataNew.Remove(group);
var locked = Fixture(4, 5);
locked.LastNormalStageCleared = 6004017;
locked.ClearedTutorialDataNew.Remove(group);
var db = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true, Users = [amplified, ordinary, ready, locked] }));
var users = (CoreInfo)db.GetProperty("Instance")!.GetValue(null)!;
using var services = new ServiceCollection().AddSingleton(new GameContext(new DbContextOptionsBuilder<GameContext>().Options)).BuildServiceProvider();
int failures = 0;

await Run("lobby snapshot resolves the interrupted basic-device tutorial at level 237", () => {
    AssertStep(LobbyHandler.CreateNetUserDataFromUser(users.Users[0]), group, terminal);
    return Task.CompletedTask;
});
await Run("returning home sends one checkpoint per group and preserves other tutorials", async () => {
    var snapshot = (await Send<ResGetUserData>(1, "/User/Get", new ReqGetUserData())).User;
    AssertStep(snapshot, group, terminal);
    AssertStep(snapshot, 3100, 310003);
    Check(snapshot.Tutorials.Count == 2, "extra tutorial records");
});
await Run("a late basic-device checkpoint cannot reactivate the tutorial after reload", async () => {
    await Send<ResSetTutorial>(1, "/tutorial/set", new ReqSetTutorial { LastClearedTid = partial });
    db.GetMethod("Reload")!.Invoke(null, null);
    var snapshot = (await Send<ResGetUserData>(1, "/User/Get", new ReqGetUserData())).User;
    AssertStep(snapshot, group, terminal);
    var stored = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))!.Users[0];
    Check(stored.ClearedTutorialDataNew[group].Id == terminal, "stale checkpoint saved");
    Check(stored.SynchroDeviceLevel == 237 && stored.SynchroSlots.Count == amplified.Characters.Count &&
        stored.Characters.Select(c => c.Level).SequenceEqual(amplified.Characters.Select(c => c.Level)), "character progression changed");
});
await Run("five level-200 characters entering the upgrade view skip the basic tutorial", async () => {
    AssertStep((await Send<ResGetUserData>(3, "/User/Get", new ReqGetUserData())).User, group, terminal);
});
await Run("four level-200 characters can continue the normal tutorial", async () => {
    AssertStep((await Send<ResGetUserData>(2, "/User/Get", new ReqGetUserData())).User, group, partial);
    await Send<ResSetTutorial>(2, "/tutorial/set", new ReqSetTutorial { LastClearedTid = 2500081 });
    AssertStep((await Send<ResGetUserData>(2, "/User/Get", new ReqGetUserData())).User, group, 2500081);
});
await Run("the unopened device does not acquire a tutorial checkpoint", async () => {
    var snapshot = (await Send<ResGetUserData>(4, "/User/Get", new ReqGetUserData())).User;
    Check(snapshot.Tutorials.All(t => t.GroupId != group), "unopened device tutorial was skipped");
});
await Run("a delayed request cannot reopen a finished basic tutorial", async () => {
    await Send<ResSetTutorial>(2, "/tutorial/set", new ReqSetTutorial { LastClearedTid = terminal });
    await Send<ResSetTutorial>(2, "/tutorial/set", new ReqSetTutorial { LastClearedTid = partial });
    AssertStep((await Send<ResGetUserData>(2, "/User/Get", new ReqGetUserData())).User, group, terminal);
});
return failures == 0 ? 0 : 1;

User Fixture(ulong id, int leveled)
{
    var user = new User { ID = id, LastNormalStageCleared = 6004018 };
    for (int i = 1; i <= 6; i++)
        user.Characters.Add(new CharacterModel { Csn = i, Level = i <= leveled ? 200 : 1 });
    user.ClearedTutorialDataNew[group] = new ClearedTutorialData { Id = partial, VersionGroup = 1 };
    user.ClearedTutorialDataNew[3100] = new ClearedTutorialData { Id = 310003 };
    return user;
}
void AssertStep(NetUserData snapshot, int groupId, int id)
{
    var steps = snapshot.Tutorials.Where(t => t.GroupId == groupId).ToList();
    Check(steps.Count == 1 && steps[0].LastClearedTid == id && steps[0].LastClearedVersion == data.TutorialTable[id].VersionGroup,
        $"expected one {id}, received [{string.Join(", ", steps.Select(t => t.LastClearedTid))}]");
}
async Task<T> Send<T>(ulong id, string route, IMessage request) where T : IMessage<T>, new()
{
    var ctx = new DefaultHttpContext { RequestServices = services };
    ctx.Items["UserID"] = id;
    ctx.Request.Path = "/v1" + route;
    ctx.Request.Body = new MemoryStream(request.ToByteArray());
    ctx.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    var response = new T();
    response.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray());
    return response;
}
async Task Run(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine($"PASS: {name}"); }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL: {name}: {e.Message}"); }
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
