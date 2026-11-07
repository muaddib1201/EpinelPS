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

const ulong account = 1401, other = 1402;
const int queen = 5185;
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
string workspace = root?.FullName ?? throw new DirectoryNotFoundException();
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var queenCharacter = data.CharacterTable.Values.First(c => c.NameCode == queen);
int[] queenIds = Enumerable.Range(3381, 20).ToArray();
var collect = data.AttractiveCounselCharacterTable[queen];
var reward = data.GetRewardTableEntry(collect.CollectRewardId);
if (args.Contains("--inspect"))
{
    Console.WriteLine(JsonConvert.SerializeObject(new { character = collect, reward,
        objects = reward.Rewards.Select(r => data.ProfileCardObjectTable.GetValueOrDefault(r.RewardId)),
        allCollectionRewardTypes = data.AttractiveCounselCharacterTable.Values.Where(r => r.CollectRewardId > 0)
            .Select(r => data.GetRewardTableEntry(r.CollectRewardId)).Select(r => new { r.UserExp, r.CharacterExp, rewards = r.Rewards.Select(i => new { i.RewardType, i.RewardValue }) }).Distinct() }));
    return 0;
}
var db = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
string save = Path.Combine(AppContext.BaseDirectory, "db.json");
void Reload() => db.GetMethod("Reload")!.Invoke(null, null);
CoreInfo State() => (CoreInfo)db.GetProperty("Instance")!.GetValue(null)!;
User Current(ulong id = account) => State().Users.Single(u => u.ID == id);
NetUserAttractiveData Bond() => Current().BondInfo.Single(b => b.NameCode == queen);
void Reset()
{
    User Player(ulong id)
    {
        var u = new User { ID = id, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow };
        u.Characters.Add(new CharacterModel { Csn = 1, Tid = queenCharacter.Id, Level = 200 });
        var b = new NetUserAttractiveData { NameCode = queen, Lv = 40, Exp = 240, CounseledCount = 61, CanCounselToday = true };
        b.CounselDialogCompleteIds.AddRange(queenIds);
        b.ObtainedRewardLevels.AddRange(new[] { 1, 3, 5, 7, 10 });
        u.BondInfo.Add(b);
        u.Currency[CurrencyType.FreeCash] = 123;
        return u;
    }
    File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true,
        LauncherTokenKey = new byte[32], EncryptionTokenKey = new byte[32], Users = [Player(account), Player(other)] }));
    Reload();
}
Reset();
using var services = new ServiceCollection().AddSingleton(new GameContext(new DbContextOptionsBuilder<GameContext>().Options)).BuildServiceProvider();
int passes = 0, failures = 0;

await Run("complete Queen collection returns and saves her actual badge sticker", async () =>
{
    var before = JToken.FromObject(State());
    var reply = await Claim();
    Check(reply.Reward != null && reply.Reward.ProfileCardObjects.SequenceEqual(new[] { 111019 }), "missing Queen badge 111019 in reward response");
    Check(Current().StickerList.SequenceEqual(new[] { 111019 }) && Current().ProfileCardsData.SequenceEqual(new[] { 111019 }), "sticker not registered as owned");
    before["Users"]![0]!["BondInfo"]![0]!["CompleteRewardStatus"] = 1;
    ((JArray)before["Users"]![0]!["StickerList"]!).Add(111019);
    ((JArray)before["Users"]![0]!["ProfileCardsData"]!).Add(111019);
    Reload();
    Check(JToken.DeepEquals(before, JToken.FromObject(State())), "claim changed unrelated account data or did not persist");
    var journal = await Send<ResGetAttractiveList>(account, "/character/attractive/get", new ReqGetAttractiveList());
    Check(journal.Attractives.Single(b => b.NameCode == queen).CompleteRewardStatus == CounselDialogCompleteRewardStatus.Received, "journal still advertises unclaimed reward");
    var owned = await Send<ResProfileCardObjectList>(account, "/ProfileCard/Possession/Get", new ReqProfileCardObjectList());
    Check(owned.StickerIds.Contains(111019), "profile inventory does not expose the claimed sticker");
});
await Run("sequential and simultaneous retries award once, including after reload", async () =>
{
    var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Claim())));
    Check(replies.Count(r => r.Reward != null) == 1, "simultaneous requests did not award exactly once");
    Reload();
    Check((await Claim()).Reward == null && Current().StickerList.Count(id => id == 111019) == 1 &&
        Current().ProfileCardsData.Count(id => id == 111019) == 1, "repeat claim paid after reload or duplicated ownership");
});
await Run("nineteen entries, duplicates and foreign entries cannot substitute for missing Queen 04", async () =>
{
    Bond().CounselDialogCompleteIds.Remove(3384);
    Bond().CounselDialogCompleteIds.Add(3381);
    Bond().CounselDialogCompleteIds.Add(3363);
    string before = JsonConvert.SerializeObject(State());
    Check((await Claim()).Reward == null, "incomplete collection was awarded");
    Check(JsonConvert.SerializeObject(State()) == before, "rejected claim changed progress");
});
await Run("unowned characters, missing bonds and unknown name codes cannot claim", async () =>
{
    Current().Characters.Clear();
    Check((await Claim()).Reward == null, "unowned character was awarded");
    Current().Characters.Add(new CharacterModel { Csn = 1, Tid = queenCharacter.Id });
    Current().BondInfo.Clear();
    Check((await Claim()).Reward == null && (await Claim(-1)).Reward == null && (await Claim(int.MaxValue)).Reward == null, "invalid character or missing bond was awarded");
    Check(Current().StickerList.Count == 0 && Current().ProfileCardsData.Count == 0, "invalid claim granted ownership");
});
await Run("an existing owned sticker is not duplicated by the first collection claim", async () =>
{
    Current().StickerList.Add(111019);
    Current().ProfileCardsData.Add(111019);
    Check((await Claim()).Reward?.ProfileCardObjects.Contains(111019) == true, "first claim was not acknowledged");
    Reload();
    Check(Bond().CompleteRewardStatus == CounselDialogCompleteRewardStatus.Received && Current().StickerList.Count == 1 && Current().ProfileCardsData.Count == 1, "ownership duplicated or claim not saved");
});
await Run("failed persistence rolls back reward ownership and claim status, then permits retry", async () =>
{
    string before = JsonConvert.SerializeObject(State()), disk = File.ReadAllText(save);
    Directory.CreateDirectory(save + ".tmp");
    bool failed = false;
    try { await Claim(); } catch { failed = true; }
    finally { Directory.Delete(save + ".tmp"); }
    Check(failed, "failed save reported success");
    Check(JsonConvert.SerializeObject(State()) == before && File.ReadAllText(save) == disk, "failed save retained partial claim state");
    Check((await Claim()).Reward?.ProfileCardObjects.Contains(111019) == true, "retry after save failure lost reward");
});
Console.WriteLine($"RESULT: {passes} passed, {failures} failed");
return failures == 0 ? 0 : 1;

Task<ResObtainCounselProgressReward> Claim(int nameCode = queen) => Send<ResObtainCounselProgressReward>(account,
    "/character/counsel/progress/reward", new ReqObtainCounselProgressReward { NameCode = nameCode });
async Task<T> Send<T>(ulong id, string route, IMessage body) where T : IMessage<T>, new()
{
    var ctx = new DefaultHttpContext { RequestServices = services };
    ctx.Items["UserID"] = id; ctx.Request.Path = "/v1" + route;
    ctx.Request.Body = new MemoryStream(body.ToByteArray()); ctx.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    return (T)new T().Descriptor.Parser.ParseFrom(((MemoryStream)ctx.Response.Body).ToArray());
}
async Task Run(string name, Func<Task> test)
{
    try { Reset(); await test(); passes++; Console.WriteLine("PASS: " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL: " + name + " — " + e.Message); }
}
static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
