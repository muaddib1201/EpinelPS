using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.LobbyServer.Character;
using EpinelPS.LobbyServer.Gacha;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
if (root == null) throw new Exception("Workspace not found");
if (args.Contains("--probe-live-readonly")) return await WeeklyMissionLiveChecks.Run(root.FullName);
var data = new GameData(Directory.GetFiles(Path.Combine(root.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
string save = Path.Combine(AppContext.BaseDirectory, "db.json");
string connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "weekly-mission.db")}";
GameContext.InitializeOptions(connection, "sqlite");
bool restarting = args.Contains("--verify-saved");
if (!restarting)
{
    File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true,
        Users = Enumerable.Range(9401, 12).Select(id => new User { ID = (ulong)id,
            LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow, InfraCoreLvl = 17 }).ToList() }));
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted(); db.Database.EnsureCreated();
    db.Users.AddRange(Enumerable.Range(9401, 12).Select(id => new GameUser { ID = (ulong)id, Nickname = "Weekly" + id }));
    db.SaveChanges();
}
var state = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
User Player(ulong id) => ((CoreInfo)state.GetValue(null)!).Users.Single(u => u.ID == id);
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
int passed = 0, failed = 0;
if (restarting)
{
    await Test("recruit and breakthrough progress survives a new process", () =>
    {
        Check(Count(9401, Trigger.GachaCharacter) == 11 && Count(9403, Trigger.CharacterGradeUpCount) == 2 &&
            Count(9404, Trigger.CharacterGradeUpCount) == 2, "saved mission progress was lost or duplicated");
        Check(Player(9403).Characters.Single().Grade == 2 && Player(9404).Characters.Single().Grade == 5,
            "saved upgraded character grades changed");
        return Task.CompletedTask;
    });
}
else
{
    var ssr = data.CharacterTable.Values.First(c => c.OriginalRare == OriginalRareType.SSR && c.GradeCoreId == 1 && c.PieceId > 0 &&
        data.CharacterTable.Values.Any(v => v.NameCode == c.NameCode && v.GradeCoreId == 11));
    data.gachaTypes.Clear(); data.GachaGradeProb.Clear(); data.GachaListProb.Clear();
    data.gachaTypes[1] = new() { Id = 1, Type = GachaPremiumType.GachaPremium, DailyFreeGachaEventId = 123,
        GradeProbId = 990001, GachaPriceGroup = [new() { GachaPriceType = 99, GachaPriceValueCount1 = 300, GachaPriceValueCount10 = 3000 }] };
    data.GachaGradeProb[990001] = new() { Id = 990001, GroupId = 990001, Rare = ssr.OriginalRare, Prob = 100, GachaListId = 990001 };
    data.GachaListProb[990001] = new() { Id = 990001, GroupId = 990001, GachaType = GachaCategory.Character, GachaId = ssr.Id, Prob = 100 };
    foreach (var id in Enumerable.Range(9401, 12)) Player((ulong)id).Currency[CurrencyType.FreeCash] = 100000;
    await Test("single and ten-pull recruit contribute eleven actual recruits", async () =>
    {
        await Recruit(9401, 1); await Recruit(9401, 10);
        Check(Count(9401, Trigger.GachaCharacter) == 11, "recruit events did not carry the actual number of recruited characters");
        var sync = await Send<ResSyncTrigger>(9401, new EpinelPS.LobbyServer.TriggerController.TriggerSync(), new ReqSyncTrigger());
        Check(sync.Triggers.Where(t => t.Trigger == 13).Sum(t => t.UserValue) == 11 &&
            sync.Triggers.Where(t => t.Trigger == 13).All(t => t.ConditionId == 0), "weekly client sync did not expose the eleven recruits");
    });
    await Test("daily free recruit contributes one and a repeated claim contributes nothing", async () =>
    {
        var request = new ReqExecuteDailyFreeGacha { GachaId = 1, EventId = 123 };
        await Send<ResExecuteDailyFreeGacha>(9402, new ExecuteEventGacha(), request);
        Check(Count(9402, Trigger.GachaCharacter) == 1, "daily free recruit was not counted");
        await Reject(() => Send<ResExecuteDailyFreeGacha>(9402, new ExecuteEventGacha(), request));
        Check(Count(9402, Trigger.GachaCharacter) == 1, "a rejected daily free repeat added progress");
    });
    await Test("limit breaking twice consumes two bodies and contributes two upgrade steps", async () =>
    {
        SetupCharacter(9403, 0, 3);
        var result = await Send<ResCharacterUpgrade>(9403, new DoLimitBreak(), new ReqCharacterUpgrade { Csn = 801, Isn = 802, Count = 2 });
        Check(result.Character.Grade == 2 && Player(9403).Items.Single().Count == 1 &&
            Count(9403, Trigger.CharacterGradeUpCount) == 2, "successful limit break did not record the consumed upgrade steps");
    });
    await Test("core enhancement uses the same weekly breakthrough counter", async () =>
    {
        SetupCharacter(9404, 3, 3);
        var result = await Send<ResCharacterCoreUpgrade>(9404, new CoreUpgrade(), new ReqCharacterCoreUpgrade { Csn = 801, Isn = 802, Count = 2 });
        Check(result.Character.Grade == 5 && Player(9404).Items.Single().Count == 1 &&
            Count(9404, Trigger.CharacterGradeUpCount) == 2, "successful core enhancement was absent from the weekly counter");
    });
    await Test("an unrelated spare body cannot upgrade a character or complete its mission", async () =>
    {
        SetupCharacter(9405, 0, 2); Player(9405).Items.Single().ItemType = -1;
        string before = JsonConvert.SerializeObject(Player(9405));
        await Reject(() => Send<ResCharacterUpgrade>(9405, new DoLimitBreak(), new ReqCharacterUpgrade { Csn = 801, Isn = 802, Count = 1 }));
        Check(JsonConvert.SerializeObject(Player(9405)) == before && Count(9405, Trigger.CharacterGradeUpCount) == 0,
            "invalid spare body changed character, inventory or mission progress");
    });
    await Test("zero and excessive upgrade counts leave the account unchanged", async () =>
    {
        SetupCharacter(9406, 3, 1);
        string before = JsonConvert.SerializeObject(Player(9406));
        await Reject(() => Send<ResCharacterCoreUpgrade>(9406, new CoreUpgrade(), new ReqCharacterCoreUpgrade { Csn = 801, Isn = 802, Count = 0 }));
        await Reject(() => Send<ResCharacterCoreUpgrade>(9406, new CoreUpgrade(), new ReqCharacterCoreUpgrade { Csn = 801, Isn = 802, Count = 2 }));
        Check(JsonConvert.SerializeObject(Player(9406)) == before && Count(9406, Trigger.CharacterGradeUpCount) == 0,
            "invalid upgrade quantity consumed inventory or added progress");
    });
    await Test("a failed recruit save cannot retain characters, costs or mission events", async () =>
    {
        string before = JsonConvert.SerializeObject(Player(9407));
        Directory.CreateDirectory(save + ".tmp");
        try { await Reject(() => Recruit(9407, 1)); }
        finally { Directory.Delete(save + ".tmp"); }
        Check(JsonConvert.SerializeObject(Player(9407)) == before && !AnyTrigger(9407),
            "failed recruit save left a character, currency charge or a ghost mission event");
    });
    await Test("a failed upgrade save cannot retain the grade or a mission event", async () =>
    {
        SetupCharacter(9408, 0, 2);
        string before = JsonConvert.SerializeObject(Player(9408));
        Directory.CreateDirectory(save + ".tmp");
        try { await Reject(() => Send<ResCharacterUpgrade>(9408, new DoLimitBreak(), new ReqCharacterUpgrade { Csn = 801, Isn = 802, Count = 1 })); }
        finally { Directory.Delete(save + ".tmp"); }
        Check(JsonConvert.SerializeObject(Player(9408)) == before && !AnyTrigger(9408),
            "failed upgrade save left a grade, consumed body or a ghost mission event");
    });
    await Test("an unfunded recruit cannot grant a character or mission progress", async () =>
    {
        Player(9409).Currency.Clear();
        string before = JsonConvert.SerializeObject(Player(9409));
        await Reject(() => Recruit(9409, 1));
        Check(JsonConvert.SerializeObject(Player(9409)) == before && !AnyTrigger(9409),
            "failed payment left rewards or progress");
    });
    await Test("dated recovery beside an old zero event can complete and claim the weekly recruit task", async () =>
    {
        var original = Player(9412).AddTrigger(Trigger.GachaCharacter, 0);
        using (var db = GameContext.CreateNew())
        {
            db.Triggers.Add(new TriggerModelNew { UserId = 9412, Type = Trigger.GachaCharacter,
                ConditionId = 0, CreatedAt = original.CreatedAt, Value = 20 });
            db.SaveChanges();
        }
        await Send<ResObtainWeeklyMissionReward>(9412, new EpinelPS.LobbyServer.Mission.ObtainWeekly(),
            new ReqObtainWeeklyMissionReward { TidList = { 20001 } });
        Check(Player(9412).WeeklyResetableData.WeeklyMissionPoints == 20 &&
            Player(9412).WeeklyResetableData.CompletedWeeklyMissions.Contains(20001),
            "a preserved zero event hid recovered recruit progress from weekly reward validation");
    });
    state.DeclaringType!.GetMethod("Save")!.Invoke(null, null);
    void SetupCharacter(ulong id, int grade, int bodies)
    {
        var variant = data.CharacterTable.Values.First(c => c.NameCode == ssr.NameCode && c.GradeCoreId == 1 + grade);
        Player(id).Characters.Add(new() { Csn = 801, Tid = variant.Id, Grade = grade, Level = 1 });
        Player(id).Items.Add(new() { Isn = 802, ItemType = ssr.PieceId, Count = bodies });
    }
}
Console.WriteLine($"WeeklyMission: {passed} PASS, {failed} FAIL");
return failed == 0 ? 0 : 1;
int Count(ulong id, Trigger type) { using var db = GameContext.CreateNew(); return db.Triggers.Where(t => t.UserId == id && t.Type == type).Sum(t => t.Value); }
bool AnyTrigger(ulong id) { using var db = GameContext.CreateNew(); return db.Triggers.Any(t => t.UserId == id); }
Task<ResExecuteGacha> Recruit(ulong id, int count) => Send<ResExecuteGacha>(id, new ExecGacha(), new ReqExecuteGacha { Tid = 1, CurrencyType = 99, Count = count });
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
async Task Reject(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("invalid or failed action was accepted"); }
async Task Test(string name, Func<Task> action) { try { await action(); passed++; Console.WriteLine("PASS: " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL: " + name + ": " + e.Message); } }
async Task<T> Send<T>(ulong id, LobbyMessage handler, IMessage req) where T : IMessage<T>, new()
{
    using var scope = services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Items["UserID"] = id;
    ctx.Request.Body = new MemoryStream(req.ToByteArray()); ctx.Response.Body = new MemoryStream();
    await handler.HandleAsync(ctx);
    return new MessageParser<T>(() => new()).ParseFrom(((MemoryStream)ctx.Response.Body).ToArray());
}
