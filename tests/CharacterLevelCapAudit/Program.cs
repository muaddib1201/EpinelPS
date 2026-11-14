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

// Diagnostic suite: uses only synthetic users and the registered session's static data.
var workspace = Environment.GetEnvironmentVariable("NIKKE_WORKSPACE_ROOT")
    ?? throw new InvalidOperationException("Run this audit through nikke_workflow.py test.");
workspace = Path.GetFullPath(workspace);
if (!Path.GetFullPath(AppContext.BaseDirectory).StartsWith(workspace + Path.DirectorySeparatorChar,
    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Audit output is outside the session.");
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"),
    "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());

var subject = data.CharacterTable.Values.First(c => c.OriginalRare == OriginalRareType.SSR && c.GradeCoreId == 1
    && new[] { 2, 3, 4, 11 }.All(g => data.CharacterTable.Values.Any(v => v.NameCode == c.NameCode && v.GradeCoreId == g)));
var anchors = data.CharacterTable.Values.Where(c => c.OriginalRare == OriginalRareType.SSR && c.GradeCoreId == 4
    && c.NameCode != subject.NameCode).GroupBy(c => c.NameCode).Select(g => g.First()).Take(5).ToArray();
if (anchors.Length != 5) throw new InvalidOperationException("Missing SSR anchor fixtures.");

var scenarios = new (int Grade, int From, int To, bool Allowed)[]
{
    (0, 159, 160, true),
    (0, 160, 161, false),
    (1, 160, 161, false),
    (2, 160, 161, false),
    (3, 199, 200, true),
    (3, 200, 201, false),
    (10, 200, 201, false),
    (0, 1, 201, false),
    (3, 199, 248, false),
};
var cases = new List<AuditCase>();
foreach (string route in new[] { "/character/levelup", "/character/synchrodevice/oneclick" })
foreach (bool enhanced in new[] { false, true })
foreach (var s in scenarios)
    cases.Add(new AuditCase((ulong)(910000 + cases.Count), route, enhanced, s.Grade, s.From, s.To, s.Allowed));

string savePath = Path.Combine(AppContext.BaseDirectory, "db.json");
var fixture = new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true };
foreach (var test in cases)
{
    var user = new User { ID = test.UserId, LastNormalStageCleared = 6004018,
        SynchroDeviceUpgraded = test.Enhanced, SynchroDeviceLevel = 248 };
    for (int i = 0; i < anchors.Length; i++)
    {
        user.Characters.Add(new CharacterModel { Csn = i + 1, Tid = anchors[i].Id, Grade = 3, Level = 200 });
        if (test.Enhanced) user.SynchroSlots.Add(new SynchroSlot { Slot = i + 1, CharacterSerialNumber = i + 1 });
    }
    var definition = data.CharacterTable.Values.Single(c => c.NameCode == subject.NameCode && c.GradeCoreId == test.Grade + 1);
    user.Characters.Add(new CharacterModel { Csn = 6, Tid = definition.Id, Grade = test.Grade, Level = test.From });
    foreach (var currency in new[] { CurrencyType.Gold, CurrencyType.CharacterExp, CurrencyType.CharacterExp2 })
        user.Currency[currency] = 2_000_000_000;
    fixture.Users.Add(user);
}
File.WriteAllText(savePath, JsonConvert.SerializeObject(fixture));
string connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "cap-audit.db")}";
GameContext.InitializeOptions(connection, "sqlite");
using (var db = GameContext.CreateNew())
{
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
    db.Users.AddRange(cases.Select(t => new GameUser { ID = t.UserId, Nickname = $"Cap audit {t.UserId}" }));
    db.SaveChanges();
}
var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
CoreInfo State() => (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
User Player(ulong id) => State().Users.Single(u => u.ID == id);
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
var results = new List<object>();
int passed = 0, failed = 0;
Console.WriteLine($"FIXTURE SSR nameCode={subject.NameCode}, template={subject.Id}; {cases.Count} request cases; session={workspace}");

foreach (var test in cases)
{
    User user = Player(test.UserId);
    int beforeGold = user.GetCurrencyVal(CurrencyType.Gold);
    int beforeData = user.GetCurrencyVal(CurrencyType.CharacterExp);
    int beforeDust = user.GetCurrencyVal(CurrencyType.CharacterExp2);
    string? error = null;
    int httpStatus = 200;
    int? responseLevel = null;
    try
    {
        using var scope = services.CreateScope();
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Items["UserID"] = test.UserId;
        ctx.Request.Path = "/v1" + test.Route;
        IMessage request = test.Route == "/character/levelup"
            ? new ReqCharacterLevelUp { Csn = 6, Lv = test.To }
            : new ReqSynchroOneClick { Chars = { new NetSynchroCharacter { Csn = 6, Lv = test.To } } };
        ctx.Request.Body = new MemoryStream(request.ToByteArray());
        ctx.Response.Body = new MemoryStream();
        await LobbyHandler.DispatchSingle(ctx);
        httpStatus = ctx.Response.StatusCode;
        byte[] body = ((MemoryStream)ctx.Response.Body).ToArray();
        responseLevel = test.Route == "/character/levelup"
            ? ResCharacterLevelUp.Parser.ParseFrom(body).Character?.Lv
            : ResSynchroOneClick.Parser.ParseFrom(body).Characters.SingleOrDefault(c => c.Csn == 6)?.Lv;
    }
    catch (BadHttpRequestException ex) when (ex.StatusCode == 400)
    {
        httpStatus = 400;
    }
    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

    user = Player(test.UserId);
    int actualLevel = user.Characters.Single(c => c.Csn == 6).Level;
    int goldSpent = beforeGold - user.GetCurrencyVal(CurrencyType.Gold);
    int dataSpent = beforeData - user.GetCurrencyVal(CurrencyType.CharacterExp);
    int dustSpent = beforeDust - user.GetCurrencyVal(CurrencyType.CharacterExp2);
    int triggers;
    using (var db = GameContext.CreateNew()) triggers = db.Triggers.Count(t => t.UserId == test.UserId);
    var saved = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(savePath))!.Users.Single(u => u.ID == test.UserId);
    int savedLevel = saved.Characters.Single(c => c.Csn == 6).Level;
    bool unregistered = user.SynchroSlots.All(s => s.CharacterSerialNumber != 6);
    bool ok = error == null && unregistered && (test.Allowed
        ? httpStatus == 200 && actualLevel == test.To && savedLevel == test.To && responseLevel == test.To && goldSpent > 0 && dataSpent > 0
        : actualLevel == test.From && savedLevel == test.From && goldSpent == 0 && dataSpent == 0 && dustSpent == 0 && triggers == 0);
    if (ok) passed++; else failed++;
    string name = $"{test.Route} enhanced={test.Enhanced} grade={test.Grade} {test.From}->{test.To}";
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}; expected={(test.Allowed ? "allow" : "reject")}, actual={actualLevel}, saved={savedLevel}, response={responseLevel}, HTTP={httpStatus}, spent={goldSpent}/{dataSpent}/{dustSpent}, triggers={triggers}, error={error ?? "none"}");
    results.Add(new { test, passed = ok, unregistered, actualLevel, savedLevel, responseLevel, httpStatus,
        goldSpent, dataSpent, dustSpent, triggers, error });
}

string output = Path.Combine(workspace, "synchro-rule-review", "server-cap-audit.json");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonConvert.SerializeObject(new { passed, failed, cases = results }, Formatting.Indented));
Console.WriteLine($"RESULT {passed} passed, {failed} failed; evidence={output}");
return failed == 0 ? 0 : 1;

sealed record AuditCase(ulong UserId, string Route, bool Enhanced, int Grade, int From, int To, bool Allowed);
