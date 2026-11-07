using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EpinelPS;
using EpinelPS.Controllers.AdminPanel;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.Models;
using EpinelPS.Services;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

const ulong target = 1201, other = 1202, legacy = 1203;
const int queen = 5185, fourth = 3384;
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
string workspace = root?.FullName ?? throw new DirectoryNotFoundException();
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var rows = await data.LoadZip<AttractiveCounselRecord>("AttractiveCounselTable.json", new Progress<double>());
var queenIds = rows.Where(r => r.NameCode == queen).Select(r => r.Id).ToArray();
var queenCharacter = data.CharacterTable.Values.First(r => r.NameCode == queen);
var anotherCharacter = data.CharacterTable.Values.First(r => r.NameCode == 5186);
var foreignDialogue = rows.First(r => r.NameCode == anotherCharacter.NameCode);
var unownedDialogue = rows.First(r => r.NameCode != queen && r.NameCode != anotherCharacter.NameCode);
if (args.Contains("--probe-live-readonly") || args.Contains("--unlock-live-queen04"))
{
    const ulong playerId = 1552239268;
    string livePath = Path.Combine(workspace, "Server and Server selector/db.json");
    var before = JObject.Parse(File.ReadAllText(livePath));
    var player = before["Users"]!.Single(u => u.Value<ulong>("ID") == playerId);
    var bond = player["BondInfo"]!.Single(b => b.Value<int>("NameCode") == queen);
    var ids = (JArray)bond["CounselDialogCompleteIds"]!;
    bool wasUnlocked = ids.Values<int>().Contains(fourth);
    bool apply = args.Contains("--unlock-live-queen04");
    if (apply && System.Diagnostics.Process.GetProcessesByName("nikke").Length != 0)
        throw new InvalidOperationException("Close the game before the one-time live unlock.");
    string liveToken = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
        .WithKey(Convert.FromBase64String(before.Value<string>("LauncherTokenKey")!), Encryption.SymmetricKey)
        .AddClaim("userId", playerId).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
    var liveCookies = new CookieContainer();
    using var liveHandler = new HttpClientHandler { CookieContainer = liveCookies, ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
    using var liveHttp = new HttpClient(liveHandler) { BaseAddress = new Uri("https://127.0.0.1/"), Timeout = TimeSpan.FromSeconds(15) };
    liveHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", liveToken);
    var options = JObject.Parse(await liveHttp.GetStringAsync($"admin/Users/{playerId}/Counsel?nameCode={queen}&lang=zh"));
    var option = options["dialogues"]!.Single(d => d.Value<int>("number") == 4);
    Check(option.Value<int>("id") == fourth && option.Value<bool>("unlocked") == wasUnlocked, "live options do not match the saved collection");
    if (apply)
    {
        string liveHtml = await liveHttp.GetStringAsync($"admin/Users/Modify/{playerId}");
        string liveCsrf = Regex.Match(liveHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Check(liveCsrf.Length > 0, "live admin page did not issue a forgery token");
        using var result = await liveHttp.PostAsync($"admin/Users/{playerId}/Counsel/unlock", new FormUrlEncodedContent([
            new("nameCode", queen.ToString()), new("counselTid", fourth.ToString()), new("__RequestVerificationToken", liveCsrf)]));
        result.EnsureSuccessStatusCode();
        var receipt = JObject.Parse(await result.Content.ReadAsStringAsync());
        Check(receipt.Value<int>("collected") == 20 && receipt.Value<int>("total") == 20, "live collection did not become 20/20");
        if (!wasUnlocked) ids.Add(fourth);
    }
    using var journalBody = new ByteArrayContent(new ReqListAnsweredAttractiveCounsel { NameCode = queen }.ToByteArray());
    journalBody.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    using var journalResponse = await liveHttp.PostAsync("v1/character/counsel/answer/list", journalBody);
    journalResponse.EnsureSuccessStatusCode();
    var journal = ResListAnsweredAttractiveCounsel.Parser.ParseFrom(await journalResponse.Content.ReadAsByteArrayAsync());
    Check(journal.CorrectlyAnsweredAttractiveCounselTableIdList.Contains(fourth) == (wasUnlocked || apply), "live game journal does not match the collection");
    var after = JObject.Parse(File.ReadAllText(livePath));
    foreach (var human in before["Users"]!.Where(u => u["ManagedNpc"] == null || u["ManagedNpc"]!.Type == JTokenType.Null))
    {
        var current = after["Users"]!.Single(u => u.Value<ulong>("ID") == human.Value<ulong>("ID"));
        Check(JToken.DeepEquals(human, current), "live action changed human account fields beyond the chosen entry");
    }
    var savedIds = after["Users"]!.Single(u => u.Value<ulong>("ID") == playerId)["BondInfo"]!
        .Single(b => b.Value<int>("NameCode") == queen)["CounselDialogCompleteIds"]!.Values<int>().ToHashSet();
    int collected = queenIds.Count(savedIds.Contains);
    var report = new { status = "verified", mode = apply ? "unlock-one-entry" : "read-only", nameCode = queen, counselTid = fourth,
        collected, total = queenIds.Length, alreadyUnlocked = wasUnlocked, humanAccountsPreserved = true, onlySelectedEntryChanged = apply && !wasUnlocked,
        gameJournalMatchesSave = true, manualClientDisplay = "pending" };
    string reportPath = Path.Combine(workspace, ".tools/counsel-unlock-20261107", apply ? "live-unlock.json" : "live-readonly.json");
    File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
    Console.WriteLine($"PASS live {(apply ? "unlock" : "read-only")} Queen 04: {collected}/{queenIds.Length}; game journal and exact human-account diff verified");
    return 0;
}
var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var savePath = Path.Combine(AppContext.BaseDirectory, "db.json");
byte[] testKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
void Reload() => jsonDb.GetMethod("Reload")!.Invoke(null, null);
CoreInfo State() => (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
User Current(ulong id) => State().Users.Single(u => u.ID == id);
void Reset()
{
    User Player(ulong id, bool bond)
    {
        var u = new User { ID = id, InfraCoreLvl = 17, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow };
        u.Characters.Add(new CharacterModel { Csn = 1, Tid = queenCharacter.Id, Level = 200 });
        u.Currency[CurrencyType.FreeCash] = 123;
        u.ResetableData.DailyCounselCount[1] = 2;
        if (bond)
        {
            var b = new NetUserAttractiveData { NameCode = queen, Lv = 40, Exp = 240, CounseledCount = 60, CanCounselToday = true, IsFavorites = true };
            b.CounselDialogCompleteIds.AddRange(queenIds.Where(id => id != fourth));
            b.ObtainedRewardLevels.AddRange(new[] { 1, 3, 5, 7, 10 });
            u.BondInfo.Add(b);
        }
        return u;
    }
    var a = Player(target, true);
    a.Characters.Add(new CharacterModel { Csn = 2, Tid = anotherCharacter.Id, Level = 1 });
    a.BondInfo.Add(new NetUserAttractiveData { NameCode = anotherCharacter.NameCode, Lv = 1, CounselDialogCompleteIds = { foreignDialogue.Id } });
    File.WriteAllText(savePath, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true, LauncherTokenKey = testKey,
        Users = [a, Player(other, true), Player(legacy, false)] }));
    Reload();
}
Reset();
const string connection = "Data Source=counsel-unlock-regression;Mode=Memory;Cache=Shared";
GameContext.InitializeOptions(connection, "sqlite");
using var sqlite = new SqliteConnection(connection);
sqlite.Open();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(UsersController).Assembly.GetName().Name });
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(connection));
builder.Services.AddSingleton<MessengerAdminService>();
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddControllersWithViews();
await using var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GameContext>();
    db.Database.EnsureCreated();
    db.SdkUsers.Add(new SdkUser { ID = target, IsAdmin = true, Email = "unlock-admin", PlayerName = "tester",
        PasswordHash = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes("unlock-test"))).ToLowerInvariant() });
    db.SdkUsers.Add(new SdkUser { ID = other, IsAdmin = false, Email = "unlock-player", PlayerName = "player", PasswordHash = "fixture" });
    db.Users.AddRange(new GameUser { ID = target }, new GameUser { ID = other }, new GameUser { ID = legacy });
    db.SaveChanges();
}
app.MapControllers();
await app.StartAsync();
var cookies = new CookieContainer();
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = new Uri(app.Urls.Single()) };
var login = await http.PostAsJsonAsync("/adminapi/login", new { username = "unlock-admin", password = "unlock-test" });
Check((await login.Content.ReadFromJsonAsync<EpinelPS.Models.Admin.LoginApiResponse>())?.OK == true, "fixture login failed");
string page = await http.GetStringAsync($"/admin/Users/Modify/{target}");
string token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
Check(token.Length > 0, "fixture antiforgery token was not issued");
int passes = 0, failures = 0;

await Run("user edit page renders an independent authenticated unlock form", async () =>
{
    var html = await http.GetStringAsync($"/admin/Users/Modify/{target}");
    Check(Regex.IsMatch(html, $"<form\\b[^>]*action=\"/admin/Users/{target}/Counsel/unlock\""), "unlock form is missing");
    int depth = 0;
    foreach (Match tag in Regex.Matches(html, "</?form\\b[^>]*>", RegexOptions.IgnoreCase))
    {
        depth += tag.Value.StartsWith("</") ? -1 : 1;
        Check(depth is 0 or 1, "unlock form is nested in another editing form");
    }
    Check(depth == 0, "forms are unbalanced");
});
await Run("option list maps Queen 04 to its real missing table ID", async () =>
{
    string before = JsonConvert.SerializeObject(State()), disk = File.ReadAllText(savePath);
    var options = await Options(target, queen);
    Check(options["characters"]!.Count() == 2 && options["dialogues"]!.Count() == 20, "owned character/consultation list is wrong");
    var fourthOption = options["dialogues"]!.Single(d => d.Value<int>("number") == 4);
    Check(fourthOption.Value<int>("id") == fourth && !fourthOption.Value<bool>("unlocked"), "04 does not map to missing 3384");
    Check(JsonConvert.SerializeObject(State()) == before && File.ReadAllText(savePath) == disk, "listing changed account data");
});
await Run("unlocking 04 changes only that character's collected ID and survives reload", async () =>
{
    var before = JToken.FromObject(State());
    var result = await Unlock(target, queen, fourth);
    Check(result.Value<int>("collected") == 20 && result.Value<int>("total") == 20 && !result.Value<bool>("alreadyUnlocked"), "wrong unlock result");
    ((JArray)before["Users"]![0]!["BondInfo"]![0]!["CounselDialogCompleteIds"]!).Add(fourth);
    Reload();
    Check(JToken.DeepEquals(before, JToken.FromObject(State())), "unlock changed counts, affinity, rewards, another character/account, or failed to persist");
});
await Run("journal and answer-list game responses expose the newly unlocked entry", async () =>
{
    await Unlock(target, queen, fourth);
    var journal = await Send<ResGetAttractiveList>(target, "/character/attractive/get", new ReqGetAttractiveList());
    var answers = await Send<ResListAnsweredAttractiveCounsel>(target, "/character/counsel/answer/list", new ReqListAnsweredAttractiveCounsel { NameCode = queen });
    Check(journal.Attractives.Single(b => b.NameCode == queen).CounselDialogCompleteIds.Contains(fourth) &&
        answers.CorrectlyAnsweredAttractiveCounselTableIdList.Contains(fourth), "client-facing reads did not include 04");
    Check(journal.CounselAvailableCount == 2, "unlock spent a consultation use");
});
await Run("repeated and simultaneous unlocks remain idempotent", async () =>
{
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Unlock(target, queen, fourth)));
    Reload();
    Check(Current(target).BondInfo.First().CounselDialogCompleteIds.Count(id => id == fourth) == 1, "concurrent unlock duplicated 04");
    var result = await Unlock(target, queen, fourth);
    Check(result.Value<bool>("alreadyUnlocked"), "repeat was not reported as already unlocked");
});
await Run("an already unlocked entry does not require another save", async () =>
{
    await Unlock(target, queen, fourth);
    Directory.CreateDirectory(savePath + ".tmp");
    try { Check((await Unlock(target, queen, fourth)).Value<bool>("alreadyUnlocked"), "repeat attempted to write again"); }
    finally { Directory.Delete(savePath + ".tmp"); }
});
await Run("missing legacy bond creates only a default collection record", async () =>
{
    await Unlock(legacy, queen, fourth);
    Reload();
    var bond = Current(legacy).BondInfo.Single();
    Check(bond.NameCode == queen && bond.Lv == 1 && bond.Exp == 0 && bond.CounseledCount == 0 && bond.CounselDialogCompleteIds.SequenceEqual(new[] { fourth }), "new bond fabricated counseling progress");
    Check(Current(legacy).ResetableData.DailyCounselCount[1] == 2 && Current(legacy).Currency[CurrencyType.FreeCash] == 123, "legacy unlock changed usage or currency");
});
await Run("failed saves roll back both existing and newly created bonds", async () =>
{
    var before = JsonConvert.SerializeObject(State()); string disk = File.ReadAllText(savePath);
    Directory.CreateDirectory(savePath + ".tmp");
    try
    {
        foreach (ulong id in new[] { target, legacy })
            Check((await Post(id, queen, fourth)).StatusCode == HttpStatusCode.InternalServerError, "failed save reported success");
    }
    finally { Directory.Delete(savePath + ".tmp"); }
    Check(JsonConvert.SerializeObject(State()) == before && File.ReadAllText(savePath) == disk, "failed save left a partial unlock");
});
await Run("invalid, cross-character and unowned entries do not change state", async () =>
{
    var before = JsonConvert.SerializeObject(State());
    foreach (var pair in new[] { (queen, 0), (queen, -1), (queen, int.MaxValue), (queen, foreignDialogue.Id), (unownedDialogue.NameCode, unownedDialogue.Id) })
        Check((await Post(target, pair.Item1, pair.Item2)).StatusCode == HttpStatusCode.BadRequest, "invalid selection was accepted");
    Check((await Post(9999, queen, fourth)).StatusCode == HttpStatusCode.NotFound, "missing account was accepted");
    Check(JsonConvert.SerializeObject(State()) == before, "invalid selection changed state");
});
await Run("authentication, CSRF and POST-only policy protect unlock and listing", async () =>
{
    var anonymousCookies = new CookieContainer();
    foreach (Cookie c in cookies.GetCookies(http.BaseAddress)) if (c.Name != "token") anonymousCookies.Add(http.BaseAddress, new Cookie(c.Name, c.Value));
    using var anon = new HttpClient(new HttpClientHandler { CookieContainer = anonymousCookies }) { BaseAddress = http.BaseAddress };
    Check((await anon.GetAsync($"/admin/Users/{target}/Counsel?nameCode={queen}")).StatusCode == HttpStatusCode.Unauthorized, "anonymous list was exposed");
    Check((await anon.PostAsync($"/admin/Users/{target}/Counsel/unlock", Form(queen, fourth))).StatusCode == HttpStatusCode.Unauthorized, "anonymous unlock accepted");
    string playerToken = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local).WithKey(testKey, Encryption.SymmetricKey)
        .AddClaim("userId", other).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
    anon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", playerToken);
    Check((await anon.GetAsync($"/admin/Users/{target}/Counsel?nameCode={queen}")).StatusCode == HttpStatusCode.Unauthorized, "non-admin listing was accepted");
    Check((await anon.PostAsync($"/admin/Users/{target}/Counsel/unlock", Form(queen, fourth))).StatusCode == HttpStatusCode.Unauthorized, "non-admin unlock accepted");
    Check((await http.PostAsync($"/admin/Users/{target}/Counsel/unlock", new FormUrlEncodedContent([new("nameCode", queen.ToString()), new("counselTid", fourth.ToString())]))).StatusCode == HttpStatusCode.BadRequest, "tokenless unlock accepted");
    Check((await http.GetAsync($"/admin/Users/{target}/Counsel/unlock")).StatusCode == HttpStatusCode.MethodNotAllowed, "GET performed an unlock");
    Check(!Current(target).BondInfo.First().CounselDialogCompleteIds.Contains(fourth), "rejected requests mutated state");
});
Console.WriteLine($"RESULT: {passes} passed, {failures} failed");
return failures == 0 ? 0 : 1;

FormUrlEncodedContent Form(int nameCode, int counselTid) => new([new("nameCode", nameCode.ToString()), new("counselTid", counselTid.ToString()), new("__RequestVerificationToken", token)]);
Task<HttpResponseMessage> Post(ulong id, int nameCode, int counselTid) => http.PostAsync($"/admin/Users/{id}/Counsel/unlock", Form(nameCode, counselTid));
async Task<JObject> Unlock(ulong id, int nameCode, int counselTid)
{
    using var response = await Post(id, nameCode, counselTid);
    Check(response.StatusCode == HttpStatusCode.OK, "unlock returned " + response.StatusCode);
    return JObject.Parse(await response.Content.ReadAsStringAsync());
}
async Task<JObject> Options(ulong id, int nameCode)
{
    using var response = await http.GetAsync($"/admin/Users/{id}/Counsel?nameCode={nameCode}");
    Check(response.StatusCode == HttpStatusCode.OK, "options returned " + response.StatusCode);
    return JObject.Parse(await response.Content.ReadAsStringAsync());
}
async Task Run(string name, Func<Task> test)
{
    try { Reset(); await test(); passes++; Console.WriteLine("PASS: " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL: " + name + " — " + e.Message); }
}
async Task<T> Send<T>(ulong id, string route, IMessage body) where T : IMessage<T>, new()
{
    using var scope = app.Services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Items["UserID"] = id; ctx.Request.Path = "/v1" + route;
    ctx.Request.Body = new MemoryStream(body.ToByteArray()); ctx.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    return (T)new T().Descriptor.Parser.ParseFrom(((MemoryStream)ctx.Response.Body).ToArray());
}
static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
