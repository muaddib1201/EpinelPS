using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

const ulong player = 1501, other = 1502;
const int queen = 5185;
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
string workspace = root?.FullName ?? throw new DirectoryNotFoundException();
var data = new GameData(Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var conversation = data.AttractiveCounselTable[3384];
var dialogs = await data.LoadZip<AttractiveCounselDialogRecord>($"AttractiveCounselDialogTable.{conversation.CounselGroupId}.json", new Progress<double>());
var preferred = dialogs.First(d => (int)d.AttractivePoint == 120);
var ordinary = dialogs.First(d => (int)d.AttractivePoint == 100);
var nonscoring = dialogs.First(d => (int)d.AttractivePoint == 0);
var queenCharacter = data.CharacterTable.Values.First(c => c.NameCode == queen);
Console.WriteLine($"FIXTURE {conversation.Id}: preferred={preferred.Id} (120), ordinary={ordinary.Id} (100)");
if (args.Contains("--probe-live-readonly")) return await CounselAffinityLiveChecks.Run(workspace,data);
if (args.Contains("--inspect"))
{
    Console.WriteLine(JsonConvert.SerializeObject(dialogs.Select(d => new { d.Id, d.GroupId, d.SpeechWindow, point = (int)d.AttractivePoint }), Formatting.Indented));
    return 0;
}
var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
string save = Path.Combine(AppContext.BaseDirectory, "db.json");
void Reload() => jsonDb.GetMethod("Reload")!.Invoke(null, null);
CoreInfo State() => (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
User Current(ulong id = player) => State().Users.Single(u => u.ID == id);
NetUserAttractiveData Bond() => Current().BondInfo.Single(b => b.NameCode == queen);
void Reset()
{
    User Account(ulong id)
    {
        var user = new User { ID = id, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow, InfraCoreLvl = 17 };
        user.Characters.Add(new CharacterModel { Csn = 1, Tid = queenCharacter.Id, Grade = 3, Level = 200 });
        user.Currency[CurrencyType.FreeCash] = 123;
        user.BondInfo.Add(new NetUserAttractiveData { NameCode = queen, Lv = 1, Exp = 0, CounseledCount = 2, IsFavorites = true, CanCounselToday = true });
        user.BondInfo.Add(new NetUserAttractiveData { NameCode = 5186, Lv = 10, Exp = 54, CounselDialogCompleteIds = { 3363 } });
        return user;
    }
    File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true,
        LauncherTokenKey = new byte[32], EncryptionTokenKey = new byte[32], Users = [Account(player), Account(other)] }));
    Reload();
    using var db = GameContext.CreateNew();
    db.Triggers.RemoveRange(db.Triggers); db.SaveChanges();
}
const string connection = "Data Source=counsel-affinity-regression;Mode=Memory;Cache=Shared";
GameContext.InitializeOptions(connection, "sqlite");
using var sqlite = new SqliteConnection(connection); sqlite.Open();
using (var db = GameContext.CreateNew())
{
    db.Database.EnsureCreated();
    db.Users.AddRange(new GameUser { ID = player, Nickname = "AffinityFixture" },new GameUser { ID = other, Nickname = "OtherFixture" });
    db.SaveChanges();
}
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
int passed = 0, failed = 0;

await Run("preferred selection grants and persists 120 with matching response", async () =>
{
    var beforeOther = JToken.FromObject(Current(other));
    var beforeBond = Current().BondInfo[1].Clone();
    var result = await Normal(preferred.Id!);
    Check(result.Exp.GainExp == 120 && result.Exp.BeforeExp == 0 && result.Exp.CurrentExp == 120 && Bond().Exp == 120, "preferred answer did not award 120");
    Reload();
    Check(Bond().Exp == 120 && Bond().CounselDialogCompleteIds.SequenceEqual(new[] { conversation.Id }), "120 or the collection entry did not persist");
    Check(JToken.DeepEquals(beforeOther,JToken.FromObject(Current(other))) && beforeBond.Equals(Current().BondInfo[1]), "another account or character changed");
    Check(Current().Currency[CurrencyType.FreeCash] == 123, "counseling changed currency");
});
await Run("ordinary selection grants and persists 100", async () =>
{
    var result = await Normal(ordinary.Id!);
    Check(result.Exp.GainExp == 100 && Bond().Exp == 100, "ordinary answer was not 100");
    Reload(); Check(Bond().Exp == 100, "ordinary affinity did not persist");
});
foreach (bool good in new[] { true, false })
await Run($"new bond uses {(good ? 120 : 100)} for its selected answer", async () =>
{
    Current().BondInfo.Remove(Bond());
    var result = await Normal((good ? preferred : ordinary).Id!);
    int expected = good ? 120 : 100;
    Check(Bond().Exp == expected && result.Exp.GainExp == expected && result.Exp.CurrentExp == expected && result.Exp.CurrentLv == Bond().Lv, "new bond gain/response is inconsistent");
});
foreach (bool fresh in new[] { false, true })
await Run($"quick counseling remains 100 for {(fresh ? "new" : "existing")} bond without inventing collected dialogs", async () =>
{
    if (fresh) Current().BondInfo.Remove(Bond());
    var result = await Send<ResCharacterQuickCounsel>(player,"/character/counsel/quick",new ReqCharacterQuickCounsel { NameCode = queen });
    Check(result.Exp.GainExp == 100 && Bond().Exp == 100 && !Bond().CounselDialogCompleteIds.Any(), "quick counseling changed its gain or fabricated collection history");
});
await Run("preferred 120 carries across a level boundary", async () =>
{
    int requirement = data.AttractiveLevelTable.Values.Single(l => l.AttractiveLevel == 2).AttractivePoint;
    Bond().Lv = 2; Bond().Exp = requirement - 100;
    var result = await Normal(preferred.Id!);
    Check(Bond().Lv == 3 && Bond().Exp == 20 && result.Exp.GainExp == 120 && result.Exp.CurrentLv == 3 && result.Exp.CurrentExp == 20, "120 level carry is incorrect");
});
await Run("a capped bond stays capped while reporting the selected answer's 120", async () =>
{
    Bond().Lv = 40; Bond().Exp = 240;
    var result = await Normal(preferred.Id!);
    Check(Bond().Lv == 40 && Bond().Exp == 240 && result.Exp.GainExp == 120, "cap handling changed or answer gain is wrong");
});
await Run("foreign group, wrong character and non-scoring rows cannot claim a 120 bonus", async () =>
{
    foreach (var request in new[] {
        new ReqCharacterCounsel { NameCode = queen, CounselTid = 3385, SelectTid = preferred.Id },
        new ReqCharacterCounsel { NameCode = 5186, CounselTid = conversation.Id, SelectTid = preferred.Id },
        new ReqCharacterCounsel { NameCode = queen, CounselTid = conversation.Id, SelectTid = nonscoring.Id },
        new ReqCharacterCounsel { NameCode = queen, CounselTid = conversation.Id, SelectTid = "unknown" },
        new ReqCharacterCounsel { NameCode = queen, CounselTid = conversation.Id }
    })
    {
        var result = await Send<ResCharacterCounsel>(player,"/character/attractive/counsel",request);
        Check(result.Exp.GainExp == 100, "an unrelated/missing choice obtained a preferred bonus");
    }
});
await Run("retrying a preferred answer replays 120 without duplicate gain or mission event", async () =>
{
    byte[] nonce = Enumerable.Range(1,24).Select(i => (byte)i).ToArray();
    var first = await Normal(preferred.Id!,nonce);
    var second = await Normal(preferred.Id!,nonce);
    Check(first.Equals(second) && Bond().Exp == 120 && Bond().CounseledCount == 3 && Bond().CounselDialogCompleteIds.Count == 1, "retry duplicated counseling or changed its response");
    using var db = GameContext.CreateNew();
    Check(db.Triggers.Count(t => t.UserId == player && t.Type == Trigger.CharacterCounsel) == 1, "retry duplicated the mission event");
});
await Run("failed persistence rolls back 120 and collection state and permits retry", async () =>
{
    string before = JsonConvert.SerializeObject(State()), disk = File.ReadAllText(save);
    Directory.CreateDirectory(save+".tmp");
    bool rejected = false;
    try { await Normal(preferred.Id!); } catch (IOException) { rejected = true; } catch (UnauthorizedAccessException) { rejected = true; }
    finally { Directory.Delete(save+".tmp"); }
    Check(rejected && before == JsonConvert.SerializeObject(State()) && disk == File.ReadAllText(save), "failed save did not roll back");
    Check((await Normal(preferred.Id!)).Exp.GainExp == 120 && Bond().Exp == 120, "retry after save failure lost the 120 gain");
});
await Run("repeat history is false before collection and true after either answer", async () =>
{
    Check(!await Seen(player,conversation.Id), "unseen dialog was marked seen");
    await Normal(ordinary.Id!);
    Check(await Seen(player,conversation.Id), "a collected dialog still appears unseen");
    Reload(); Check(await Seen(player,conversation.Id), "seen history did not persist");
});
await Run("repeat history isolates accounts and rejects unknown/cross-character collection IDs", async () =>
{
    Bond().CounselDialogCompleteIds.Add(conversation.Id);
    Check(await Seen(player,conversation.Id) && !await Seen(other,conversation.Id), "seen history leaked across accounts");
    Check(!await Seen(player,-1) && !await Seen(player,int.MaxValue), "unknown conversation was marked seen");
    Bond().CounselDialogCompleteIds.Add(3364);
    Check(!await Seen(player,3364), "foreign character's corrupted collection entry was trusted");
    string before = JsonConvert.SerializeObject(State()), disk = File.ReadAllText(save);
    await Seen(player,conversation.Id);
    Check(before == JsonConvert.SerializeObject(State()) && disk == File.ReadAllText(save), "checking history modified the account");
});
await Run("a queued repeat-history read ignores a collection rolled back by failed persistence", () =>
{
    var stale = Current();
    bool? seen = null; Exception? readFailure = null;
    var reader = new Thread(() =>
    {
        try { seen = Seen(player,conversation.Id).GetAwaiter().GetResult(); }
        catch(Exception e) { readFailure = e; }
    }) { IsBackground = true };
    var apply = typeof(User).Assembly.GetType("EpinelPS.LobbyServer.Character.Counsel.CounselMissionDelivery")!
        .GetMethod("Apply",BindingFlags.Static|BindingFlags.NonPublic)!.MakeGenericMethod(typeof(ResCharacterCounsel));
    var saveLock = jsonDb.GetField("SaveLock",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    Directory.CreateDirectory(save+".tmp");
    bool failedSave = false;
    try
    {
        lock(stale)
        lock(saveLock)
        {
            try
            {
                apply.Invoke(null,new object?[] { stale,new ReqCharacterCounsel { NameCode = queen,CounselTid = conversation.Id },null,
                    new Func<ResCharacterCounsel>(() =>
                    {
                        stale.BondInfo.Single(b => b.NameCode == queen).CounselDialogCompleteIds.Add(conversation.Id);
                        reader.Start();
                        Check(SpinWait.SpinUntil(() => (reader.ThreadState & ThreadState.WaitSleepJoin) != 0,TimeSpan.FromSeconds(5)),"queued reader did not wait for the in-flight user lock");
                        return new ResCharacterCounsel();
                    }) });
            }
            catch(TargetInvocationException e) when(e.InnerException is IOException or UnauthorizedAccessException) { failedSave = true; }
        }
    }
    finally { Directory.Delete(save+".tmp"); }
    Check(reader.Join(TimeSpan.FromSeconds(5)),"queued history read did not complete");
    Check(failedSave && readFailure == null && seen == false && !ReferenceEquals(stale,Current()) && !Bond().CounselDialogCompleteIds.Contains(conversation.Id),"queued read exposed the rolled-back collection instead of committed history");
    return Task.CompletedTask;
});
Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

Task<ResCharacterCounsel> Normal(string selection,byte[]? nonce = null) => Send<ResCharacterCounsel>(player,"/character/attractive/counsel",new ReqCharacterCounsel { NameCode = queen,CounselTid = conversation.Id,SelectTid = selection },nonce);
async Task<bool> Seen(ulong id,int tid) => (await Send<ResCounseledBefore>(id,"/character/counsel/check",new ReqCounseledBefore { CounselTid = tid })).IsCounseledBefore;
async Task<T> Send<T>(ulong id,string route,IMessage body,byte[]? nonce = null) where T:IMessage<T>,new()
{
    using var scope = services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Items["UserID"] = id; if (nonce != null) ctx.Items["Nonce"] = nonce;
    ctx.Request.Path = "/v1"+route; ctx.Request.Body = new MemoryStream(body.ToByteArray()); ctx.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    return (T)new T().Descriptor.Parser.ParseFrom(((MemoryStream)ctx.Response.Body).ToArray());
}
async Task Run(string name,Func<Task> action)
{
    try { Reset(); await action(); passed++; Console.WriteLine("PASS: "+name); }
    catch(Exception e) { failed++; Console.WriteLine("FAIL: "+name+" — "+e.Message); }
}
static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
