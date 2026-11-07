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

// Only the static tables are shared with the deployment. The save belongs to this test binary.
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
var progress = new Progress<double>();
foreach (var row in await data.LoadZip<AttractiveLevelRewardRecord>("AttractiveLevelRewardTable.json", progress))
    data.AttractiveLevelReward.Add(row.Id, row);
foreach (var row in await data.LoadZip<RewardRecord>("RewardTable.json", progress))
    data.RewardDataRecords.Add(row.Id, row);

const ulong account = 1001, secondAccount = 1002;
const int queen = 5185, rapi = 3001, neon = 3002, anis = 3005;
int[] allLevels = [1, 3, 5, 7, 10];
var original = new User { ID = account };
original.Currency[CurrencyType.FreeCash] = 1000;
original.BondInfo.AddRange([Bond(queen, 40, 966, 967, 968, 969, 970), Bond(rapi, 10, 1, 2, 3),
    Bond(anis, 1)]);
original.CompletedScenarios.AddRange(Enumerable.Range(1, 5).Select(i => $"d_nikke_marian_queen_{i:00}"));
// Seeing a scenario and collecting its reward are separate requests.
original.CompletedScenarios.Add("d_nikke_anis_01");
var other = new User { ID = secondAccount };
other.BondInfo.Add(Bond(neon, 10, 6, 7));
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, Users = [original, other] }));
var db = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var instance = db.GetProperty("Instance")!;
using var services = new ServiceCollection().AddSingleton(new GameContext(new DbContextOptionsBuilder<GameContext>().Options)).BuildServiceProvider();
var failures = new List<string>();

await Verify("overlapping journal requests return each account's own characters", async () =>
{
    using var reading = new ManualResetEventSlim();
    using var resume = new ManualResetEventSlim();
    using var body = new PausedReadStream(reading, resume);
    var first = Task.Run(() => Send<ResGetAttractiveList>(account, "/v1/character/attractive/get", body));
    try
    {
        Check(reading.Wait(TimeSpan.FromSeconds(10)), "first request did not start");
        var second = await Journal(secondAccount, neon);
    }
    finally { resume.Set(); }
    Check((await first).Attractives.Any(b => b.NameCode == queen), "first request lost its account or response");
});
await Verify("five old claims reach the journal and disk without paying again", async () =>
{
    EqualLevels(allLevels, await Journal(account, queen));
    var saved = ReadSaved(account);
    EqualLevels(allLevels, saved.BondInfo.Single(b => b.NameCode == queen).ObtainedRewardLevels);
    Check(saved.GetCurrencyVal(CurrencyType.FreeCash) == 1000, "migration changed the wallet");
    Check(saved.CompletedScenarios.SequenceEqual(original.CompletedScenarios), "migration changed watched scenarios");
});
await Verify("overlapping row IDs map by character, across accounts and reloads", async () =>
{
    EqualLevels([1, 3, 5], await Journal(account, rapi));
    EqualLevels([1, 3], await Journal(secondAccount, neon));
    Reload();
    EqualLevels([1, 3, 5], await Journal(account, rapi));
    EqualLevels([1, 3], await Journal(secondAccount, neon));
    EqualLevels([], await Journal(account, anis));
});
await Verify("replaying all five migrated episodes pays zero", async () =>
{
    foreach (int level in allLevels)
        Check((await Claim(queen, level)).Reward == null, $"level {level} paid twice");
    Check(CurrentUser().GetCurrencyVal(CurrencyType.FreeCash) == 1000, "replay changed the wallet");
});
await Verify("first claim and simultaneous retries pay exactly once and survive reload", async () =>
{
    var requests = Enumerable.Range(0, 8).Select(_ => Task.Run(() => Claim(anis, 1)));
    var replies = await Task.WhenAll(requests);
    Check(replies.Count(r => r.Reward != null) == 1, "wrong number of rewards");
    var currency = replies.Single(r => r.Reward != null).Reward.Currency.Single();
    Check(currency.Type == (int)CurrencyType.FreeCash && currency.Value == 50 && currency.FinalValue == 1050, "wrong payout");
    Reload();
    EqualLevels([1], await Journal(account, anis));
    Check((await Claim(anis, 1)).Reward == null, "claim paid again after reload");
    Check(ReadSaved(account).GetCurrencyVal(CurrencyType.FreeCash) == 1050, "wrong saved balance");
});
await Verify("unavailable ranks and unknown rewards do not grant or change progress", async () =>
{
    foreach (var (code, level) in new[] { (anis, 3), (anis, 12), (99999, 1) })
        Check((await Claim(code, level)).Reward == null, "invalid claim paid out");
    EqualLevels([1], await Journal(account, anis));
    Check(CurrentUser().GetCurrencyVal(CurrencyType.FreeCash) == 1050, "invalid claim changed the wallet");
});
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));

static NetUserAttractiveData Bond(int code, int rank, params int[] oldClaims)
{
    var bond = new NetUserAttractiveData { NameCode = code, Lv = rank };
    bond.ObtainedRewardLevels.AddRange(oldClaims);
    return bond;
}
User ReadSaved(ulong id) => JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))!.Users.Single(u => u.ID == id);
User CurrentUser() => ((CoreInfo)instance.GetValue(null)!).Users.Single(u => u.ID == account);
void Reload() => db.GetMethod("Reload")!.Invoke(null, null);
Task<ResObtainAttractiveReward> Claim(int code, int level) => Send<ResObtainAttractiveReward>(account,
    "/v1/character/attractive/obtainreward", new MemoryStream(new ReqObtainAttractiveReward { NameCode = code, Lv = level }.ToByteArray()));
async Task<int[]> Journal(ulong id, int code)
{
    var response = await Send<ResGetAttractiveList>(id, "/v1/character/attractive/get", new MemoryStream());
    return response.Attractives.Single(b => b.NameCode == code).ObtainedRewardLevels.ToArray();
}
async Task<T> Send<T>(ulong id, string route, Stream body) where T : IMessage<T>, new()
{
    var context = new DefaultHttpContext { RequestServices = services };
    context.Items["UserID"] = id;
    context.Request.Path = route;
    context.Request.Body = body;
    context.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(context);
    var result = new T();
    result.MergeFrom(((MemoryStream)context.Response.Body).ToArray());
    return result;
}
async Task Verify(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine($"PASS: {name}"); }
    catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); Console.WriteLine($"FAIL: {failures[^1]}"); }
}
static void Check(bool condition, string failure)
{
    if (!condition) throw new Exception(failure);
}
static void EqualLevels(IEnumerable<int> expected, IEnumerable<int> actual) =>
    Check(expected.SequenceEqual(actual), $"expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");

sealed class PausedReadStream(ManualResetEventSlim reading, ManualResetEventSlim resume) : MemoryStream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        reading.Set();
        if (!resume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("second request did not complete");
        return base.Read(buffer, offset, count);
    }
}
