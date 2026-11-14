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

// Real-handler regression: uses only synthetic users and the registered session's static data.
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
    && new[] { 2, 3, 4, 5, 11 }.All(g => data.CharacterTable.Values.Any(v => v.NameCode == c.NameCode && v.GradeCoreId == g)));
var anchors = data.CharacterTable.Values.Where(c => c.OriginalRare == OriginalRareType.SSR && c.GradeCoreId == 4
    && c.NameCode != subject.NameCode).GroupBy(c => c.NameCode).Select(g => g.First()).Take(5).ToArray();
if (anchors.Length != 5) throw new InvalidOperationException("Missing SSR anchor fixtures.");
var prefixCharacter = data.CharacterTable.Values.First(c => c.OriginalRare == OriginalRareType.SSR && c.GradeCoreId == 1
    && c.NameCode != subject.NameCode && anchors.All(a => a.NameCode != c.NameCode));

var scenarios = new (int Grade, int From, int To, bool Allowed, int? StoredGrade)[]
{
    (0, 159, 160, true, null),
    (0, 160, 161, false, null),
    (1, 159, 160, true, null),
    (1, 160, 161, false, null),
    (2, 159, 160, true, null),
    (2, 160, 161, false, null),
    (3, 199, 200, true, null),
    (3, 200, 201, false, null),
    (4, 199, 200, true, null),
    (4, 200, 201, false, null),
    (10, 199, 200, true, null),
    (10, 200, 201, false, null),
    (0, 1, 201, false, null),
    (3, 199, 248, false, null),
    (0, 160, int.MaxValue, false, null),
    (3, 200, int.MaxValue, false, null),
    (0, 161, 162, false, null),
    (3, 201, 202, false, null),
    (0, 160, 161, false, 10),
    (3, 199, 200, true, 0),
};
var cases = new List<AuditCase>();
foreach (string route in new[] { "/character/levelup", "/character/synchrodevice/oneclick" })
foreach (bool enhanced in new[] { false, true })
foreach (var s in scenarios)
    cases.Add(new AuditCase((ulong)(910000 + cases.Count), route, enhanced, s.Grade, s.From, s.To, s.Allowed, s.StoredGrade));
foreach (bool enhanced in new[] { false, true })
{
    foreach (string prefix in new[] { "separate-before", "separate-after", "duplicate-before" })
        cases.Add(new AuditCase((ulong)(910000 + cases.Count), "/character/synchrodevice/oneclick", enhanced,
            0, prefix == "duplicate-before" ? 159 : 160, 161, false, Prefix: prefix));
    cases.Add(new AuditCase((ulong)(910000 + cases.Count), "/character/synchrodevice/oneclick", enhanced,
        0, 159, 160, true, Prefix: "separate-before"));
    foreach (string route in new[] { "/character/levelup", "/character/synchrodevice/oneclick" })
        cases.Add(new AuditCase((ulong)(910000 + cases.Count), route, enhanced, 0, 1, 2, false, UnknownTemplate: true));
}

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
    if (test.Prefix.StartsWith("separate"))
        user.Characters.Add(new CharacterModel { Csn = 7, Tid = prefixCharacter.Id, Grade = 0, Level = 159 });
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
string TriggerSnapshot(ulong id)
{
    using var db = GameContext.CreateNew();
    return JsonConvert.SerializeObject(db.Triggers.Where(t => t.UserId == id)
        .OrderBy(t => t.CreatedAt).ThenBy(t => t.Type).ThenBy(t => t.ConditionId).ThenBy(t => t.Value)
        .Select(t => new { t.Type, t.ConditionId, t.Value, t.CreatedAt }).ToArray());
}
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
var results = new List<object>();
int passed = 0, failed = 0;
Console.WriteLine($"FIXTURE SSR nameCode={subject.NameCode}, template={subject.Id}; {cases.Count} request cases; session={workspace}");

foreach (var test in cases)
{
    User user = Player(test.UserId);
    if (test.StoredGrade.HasValue || test.UnknownTemplate)
    {
        var character = user.Characters.Single(c => c.Csn == 6);
        if (test.StoredGrade.HasValue) character.Grade = test.StoredGrade.Value;
        if (test.UnknownTemplate) character.Tid = int.MaxValue;
        jsonDb.GetMethod("Save")!.Invoke(null, null);
    }
    string memoryBefore = JsonConvert.SerializeObject(user);
    byte[] saveBefore = File.ReadAllBytes(savePath);
    string triggersBefore = TriggerSnapshot(test.UserId);
    long beforeGold = user.GetCurrencyVal(CurrencyType.Gold);
    long beforeData = user.GetCurrencyVal(CurrencyType.CharacterExp);
    long beforeDust = user.GetCurrencyVal(CurrencyType.CharacterExp2);
    string? error = null;
    int httpStatus = 200;
    int? responseLevel = null;
    try
    {
        using var scope = services.CreateScope();
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Items["UserID"] = test.UserId;
        ctx.Request.Path = "/v1" + test.Route;
        IMessage request;
        if (test.Route == "/character/levelup") request = new ReqCharacterLevelUp { Csn = 6, Lv = test.To };
        else
        {
            var batch = new ReqSynchroOneClick();
            var main = new NetSynchroCharacter { Csn = 6, Lv = test.To };
            var prefix = new NetSynchroCharacter { Csn = test.Prefix == "duplicate-before" ? 6 : 7, Lv = 160 };
            if (test.Prefix.EndsWith("before")) batch.Chars.Add(prefix);
            batch.Chars.Add(main);
            if (test.Prefix.EndsWith("after")) batch.Chars.Add(prefix);
            request = batch;
        }
        ctx.Request.Body = new MemoryStream(request.ToByteArray());
        ctx.Response.Body = new MemoryStream();
        await LobbyHandler.DispatchSingle(ctx);
        httpStatus = ctx.Response.StatusCode;
        byte[] body = ((MemoryStream)ctx.Response.Body).ToArray();
        responseLevel = test.Route == "/character/levelup"
            ? ResCharacterLevelUp.Parser.ParseFrom(body).Character?.Lv
            : ResSynchroOneClick.Parser.ParseFrom(body).Characters.LastOrDefault(c => c.Csn == 6)?.Lv;
    }
    catch (BadHttpRequestException ex) when (ex.StatusCode == 400)
    {
        httpStatus = 400;
    }
    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

    user = Player(test.UserId);
    int actualLevel = user.Characters.Single(c => c.Csn == 6).Level;
    long goldSpent = beforeGold - user.GetCurrencyVal(CurrencyType.Gold);
    long dataSpent = beforeData - user.GetCurrencyVal(CurrencyType.CharacterExp);
    long dustSpent = beforeDust - user.GetCurrencyVal(CurrencyType.CharacterExp2);
    int triggers;
    using (var db = GameContext.CreateNew()) triggers = db.Triggers.Count(t => t.UserId == test.UserId);
    var saved = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(savePath))!.Users.Single(u => u.ID == test.UserId);
    int savedLevel = saved.Characters.Single(c => c.Csn == 6).Level;
    bool unregistered = user.SynchroSlots.All(s => s.CharacterSerialNumber != 6);
    bool memoryUnchanged = memoryBefore == JsonConvert.SerializeObject(user);
    bool saveUnchanged = saveBefore.SequenceEqual(File.ReadAllBytes(savePath));
    bool triggersUnchanged = triggersBefore == TriggerSnapshot(test.UserId);
    var costs = data.GetCharacterLevelUpData().Values.Where(r => r.Level > test.From && r.Level <= test.To).ToArray();
    int factor = test.Allowed && test.Prefix.StartsWith("separate") ? 2 : 1;
    bool expectedMaterials = goldSpent == factor * costs.Sum(r => (long)r.Gold)
        && dataSpent == factor * costs.Sum(r => (long)r.CharacterExp)
        && dustSpent == factor * costs.Sum(r => (long)r.CharacterExp2);
    bool prefixUpgraded = !test.Prefix.StartsWith("separate")
        || user.Characters.Single(c => c.Csn == 7).Level == 160 && saved.Characters.Single(c => c.Csn == 7).Level == 160;
    bool ok = error == null && unregistered && (test.Allowed
        ? httpStatus == 200 && actualLevel == test.To && savedLevel == test.To && responseLevel == test.To && expectedMaterials && prefixUpgraded
        : httpStatus == 400 && actualLevel == test.From && savedLevel == test.From && goldSpent == 0 && dataSpent == 0
            && dustSpent == 0 && triggersUnchanged && memoryUnchanged && saveUnchanged);
    if (ok) passed++; else failed++;
    string name = $"{test.Route} enhanced={test.Enhanced} grade={test.Grade} storedGrade={test.StoredGrade} {test.From}->{test.To} batch={test.Prefix} unknown={test.UnknownTemplate}";
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}; expected={(test.Allowed ? "allow" : "reject")}, actual={actualLevel}, saved={savedLevel}, response={responseLevel}, HTTP={httpStatus}, spent={goldSpent}/{dataSpent}/{dustSpent}, triggers={triggers}, triggersUnchanged={triggersUnchanged}, memoryUnchanged={memoryUnchanged}, saveUnchanged={saveUnchanged}, error={error ?? "none"}");
    results.Add(new { test, passed = ok, unregistered, actualLevel, savedLevel, responseLevel, httpStatus,
        goldSpent, dataSpent, dustSpent, triggers, triggersUnchanged, memoryUnchanged, saveUnchanged, expectedMaterials, prefixUpgraded, error });
}

string output = Path.Combine(workspace, "synchro-rule-review", $"level-cap-regression-{Guid.NewGuid():N}.json");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonConvert.SerializeObject(new { passed, failed, cases = results }, Formatting.Indented));
Console.WriteLine($"RESULT {passed} passed, {failed} failed; evidence={output}");
return failed == 0 ? 0 : 1;

sealed record AuditCase(ulong UserId, string Route, bool Enhanced, int Grade, int From, int To, bool Allowed,
    int? StoredGrade = null, string Prefix = "", bool UnknownTemplate = false);
