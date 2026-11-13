// Isolated diagnosis of the deployed payment behavior, not an acceptance test for a fix.
// No live account, network request, or deployment is used.
using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.LobbyServer.Gacha;
using EpinelPS.Models;
using EpinelPS.Utils;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

string root = Environment.GetEnvironmentVariable("NIKKE_WORKSPACE_ROOT")
    ?? throw new InvalidOperationException("Run through the session test workflow.");
if (!Path.GetFullPath(root).Contains(Path.DirectorySeparatorChar + ".worktrees" + Path.DirectorySeparatorChar))
    throw new InvalidOperationException("An isolated session workspace is required.");
var output = Console.Out;
Console.SetOut(TextWriter.Null);
string save = Path.Combine(AppContext.BaseDirectory, "db.json");
File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo
{
    DbVersion = 5, BondRewardLevelsMigrated = true, LogLevel = LogType.Error,
    LauncherTokenKey = new byte[32], EncryptionTokenKey = new byte[32], ActiveEventBannerIds = [10124]
}));
string pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
Logging.SetOutputLevel(LogType.Error);
string connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "diagnosis.db")}";
GameContext.InitializeOptions(connection, "sqlite");
using (var db = GameContext.CreateNew()) { db.Database.EnsureDeleted(); db.Database.EnsureCreated(); }
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
var jsonType = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var state = (CoreInfo)jsonType.GetProperty("Instance")!.GetValue(null)!;
var banner = data.gachaTypes[10094];
var choices = data.GachaSelectupListTable.Values.Where(c => c.GachaTypeId == banner.Id).OrderBy(c => c.Id).ToArray();
var report = new JObject
{
    ["banner"] = JObject.FromObject(banner),
    ["choices"] = JArray.FromObject(choices.Select(c => new { choice = c, character = new
        { data.CharacterTable[c.CharacterId].Id, data.CharacterTable[c.CharacterId].NameCode, data.CharacterTable[c.CharacterId].NameLocalkey } })),
    ["cases"] = new JArray()
};
var cases = (JArray)report["cases"]!;
int passed = 0, failed = 0;
ulong nextUser = 980000;
var grades = data.GachaGradeProb.Values.Where(g => g.GroupId == banner.GradeProbId).ToArray();
var original = grades.ToDictionary(g => g.Id, g => g.Prob);
var upGrade = grades.Single(g => data.GachaListProb.Values.Any(r => r.GroupId == g.GachaListId && r.GachaType == GachaCategory.GachaSelectup));
try
{
    // Deterministic selected-character branch, so every character's grant and payment is exercised.
    foreach (var g in grades) g.Prob = g.Id == upGrade.Id ? 10000 : 0;
    foreach (var choice in choices)
    foreach (int count in new[] { 1, 10 })
    foreach (int currency in new[] { 5503, 99 })
    {
        var user = new User
        {
            ID = ++nextUser, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow,
            Currency = new() { [(CurrencyType)5503] = 10, [CurrencyType.FreeCash] = 100000, [CurrencyType.ChargeCash] = 10000 }
        };
        state.Users.Add(user);
        using (var db = GameContext.CreateNew())
        { db.Users.Add(new GameUser { ID = user.ID, Nickname = "Synthetic recruit diagnosis" }); db.SaveChanges(); }
        await Send<ResSetGachaSelectup>(new SetGachaSelectSetup(), user.ID, new ReqSetGachaSelectup
        { GachaSelectupData = new() { GachaTypeId = banner.Id, GachaSelectupId = choice.Id } });
        var before = JObject.FromObject(user);
        string diskBefore = File.ReadAllText(save);
        int triggersBefore;
        using (var db = GameContext.CreateNew()) triggersBefore = db.Triggers.Count(t => t.UserId == user.ID);
        var row = new JObject { ["choiceId"] = choice.Id, ["characterId"] = choice.CharacterId,
            ["currency"] = currency, ["count"] = count, ["ticketBefore"] = user.GetCurrencyVal((CurrencyType)5503),
            ["gemsBefore"] = user.GetCurrencyVal(CurrencyType.FreeCash) };
        try
        {
            var response = await Send<ResExecuteGacha>(new ExecGacha(), user.ID,
                new ReqExecuteGacha { Tid = banner.Id, Count = count, CurrencyType = currency });
            row["outcome"] = "success";
            row["characters"] = JArray.FromObject(response.Gacha.Select(g => g.Tid));
            row["responseCurrencies"] = JArray.FromObject(response.Currencies.Select(c => new { c.Type, c.Value }));
            long unitCost = banner.GachaPriceGroup.First(p => p.GachaPriceType == 99).GachaPriceValueCount1;
            Check(currency == 99, "special ticket unexpectedly succeeded; update the diagnosis expectations");
            Check(response.Gacha.Count == count && response.Gacha.All(c => c.Tid == choice.CharacterId), "wrong selected character");
            Check(user.HasCharacter(choice.CharacterId), "selected character missing from inventory");
            Check(user.GetCurrencyVal(CurrencyType.FreeCash) == 100000 - count * unitCost, "wrong gem charge");
            Check(user.GetCurrencyVal((CurrencyType)5503) == 10, "gem request changed select-up tickets");
            passed++;
            output.WriteLine($"PASS choice={choice.Id} currency={currency} count={count}: selected character delivered, gems charged");
        }
        catch (BadHttpRequestException ex) when (currency == 5503 && ex.Message == "Unsupported recruit payment currency.")
        {
            row["outcome"] = "unsupported-payment"; row["status"] = ex.StatusCode; row["message"] = ex.Message;
            bool memoryUnchanged = JToken.DeepEquals(before, JObject.FromObject(user));
            bool diskUnchanged = diskBefore == File.ReadAllText(save);
            int triggersAfter;
            using (var db = GameContext.CreateNew()) triggersAfter = db.Triggers.Count(t => t.UserId == user.ID);
            row["memoryUnchanged"] = memoryUnchanged; row["saveUnchanged"] = diskUnchanged;
            row["triggerCountUnchanged"] = triggersBefore == triggersAfter;
            if (!memoryUnchanged || !diskUnchanged || triggersBefore != triggersAfter) failed++;
            else passed++;
            output.WriteLine($"{(memoryUnchanged && diskUnchanged && triggersBefore == triggersAfter ? "PASS" : "FAIL")} choice={choice.Id} currency={currency} count={count}: reproduced {ex.StatusCode}, rollback verified={memoryUnchanged && diskUnchanged && triggersBefore == triggersAfter}");
        }
        catch (Exception ex) { failed++; row["outcome"] = "unexpected-error"; row["message"] = ex.ToString(); output.WriteLine("FAIL " + ex); }
        row["ticketAfter"] = user.GetCurrencyVal((CurrencyType)5503);
        row["gemsAfter"] = user.GetCurrencyVal(CurrencyType.FreeCash);
        cases.Add(row);
    }
}
finally { foreach (var g in grades) g.Prob = original[g.Id]; Console.SetOut(output); }
report["passed"] = passed; report["failed"] = failed;
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diagnosis.json"), report.ToString());
Console.WriteLine($"RESULT {passed} verified diagnostic cases, {failed} unexpected failures; unsupported ticket payments remain unfixed.");
return failed == 0 ? 0 : 1;

async Task<T> Send<T>(LobbyMessage handler, ulong id, IMessage request) where T : IMessage<T>, new()
{
    using var scope = services.CreateScope();
    var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    context.Items["UserID"] = id;
    context.Request.Body = new MemoryStream(request.ToByteArray());
    context.Response.Body = new MemoryStream();
    await handler.HandleAsync(context);
    var response = new T(); response.MergeFrom(((MemoryStream)context.Response.Body).ToArray()); return response;
}
static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
