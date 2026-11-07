using System.Net;
using System.Net.Http.Json;
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

const ulong target = 1001, other = 1002;
var liveRoot = args.FirstOrDefault(a => a.StartsWith("--probe-live-root="));
if (liveRoot != null)
{
    await ProbeLive(liveRoot["--probe-live-root=".Length..]);
    return;
}
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
if (args.Contains("--verify-saved"))
{
    var saved = JObject.Parse(File.ReadAllText(save));
    var player = saved["Users"]!.Single(u => u.Value<ulong>("ID") == target)!;
    var savedActive = player["LiberateDatas"]![player.Value<int>("CurCharacterIdId").ToString()]!;
    Check(savedActive.Value<int>("ProgressPoint") == 175 && savedActive.Value<int>("Step") == 2 &&
        savedActive.Value<int>("RewardedCount") == 0 && savedActive["MissionData"]!.All(m => m.Value<int>("MissionState") == 0),
        "reset state did not survive a separate process");
    Console.WriteLine("PASS: reset missions, points and stage survive a separate process");
    return;
}

var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
foreach (var row in await data.LoadZip<LiberateCharacterRecord>("LiberateCharacterTable.json", new Progress<double>())) data.LiberateCharacterTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<LiberateMissionGroupRecord>("LiberateMissionGroupTable.json", new Progress<double>())) data.LiberateMissionGroupTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<LiberateMissionRecord>("LiberateMissionTable.json", new Progress<double>())) data.LiberateMissionTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<InfraCoreGradeRecord>("InfraCoreGradeTable.json", new Progress<double>())) data.InfracoreTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<DispatchBoardRecord>("DispatchBoardTable.json", new Progress<double>())) data.DispatchBoardTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<AttractiveLevelRecord>("AttractiveLevelTable.json", new Progress<double>())) data.AttractiveLevelTable.Add(row.Id, row);
var character = data.LiberateCharacterTable.Values.OrderBy(c => c.Id).First();
var group = data.LiberateMissionGroupTable.Values.Single(g => g.MissionStepGroup == character.MissionStepGroup && g.MissionStep == 2);
var first = new User { ID = target, InfraCoreLvl = 17, CurCharacterIdId = character.Id };
var second = new User { ID = other, InfraCoreLvl = 1 };
first.Currency[CurrencyType.FreeCash] = 345;
first.ResetableData.DailyMissionPoints = 40;
first.OpenLiberateTypeIdList.Add(character.TypeGroupId);
var active = new NetLiberateData { CharacterId = character.Id, Step = 2, ProgressPoint = 175, RewardedCount = character.MissionMaxClearAmount };
int slot = 1;
foreach (var mission in data.LiberateMissionTable.Values.Where(m => m.GroupId == group.DefaultMissionGroupId).Take(3))
    active.MissionData.Add(new NetLiberateMissionData { Id = slot++, MissionTid = mission.Id, LiberateCharacterId = character.Id,
        MissionState = LiberateMissionState.Rewarded, CreatedAt = DateTime.UtcNow.AddHours(-1).Ticks,
        ReceivedAt = DateTime.UtcNow.AddHours(-1).Ticks, TriggerStartAt = DateTime.UtcNow.AddHours(-1).Ticks, TriggerEndAt = DateTime.UtcNow.AddHours(23).Ticks });
first.LiberateDatas.Add(character.Id, active);
first.LiberateDatas.Add(9999, new NetLiberateData { CharacterId = 9999, Step = 4, ProgressPoint = 91, RewardedCount = 2 });
File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true, Users = [first, second] }));
var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
User Current(ulong id) => ((CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!).Users.Single(u => u.ID == id);
void Reload() => jsonDb.GetMethod("Reload")!.Invoke(null, null);

const string connection = "Data Source=liberate-reset-regression;Mode=Memory;Cache=Shared";
GameContext.InitializeOptions(connection, "sqlite");
using var sqlite = new SqliteConnection(connection);
sqlite.Open();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(UsersController).Assembly.GetName().Name });
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Error);
builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(connection));
builder.Services.AddSingleton<MessengerAdminService>();
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddControllersWithViews();
await using var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GameContext>();
    db.Database.EnsureCreated();
    db.SdkUsers.Add(new SdkUser { ID = target, IsAdmin = true, Email = "tester", PlayerName = "tester",
        PasswordHash = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes("local-test"))).ToLowerInvariant() });
    db.Users.AddRange(new GameUser { ID = target }, new GameUser { ID = other });
    db.SaveChanges();
}
app.MapControllers();
await app.StartAsync();
var cookies = new CookieContainer();
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = new Uri(app.Urls.Single()) };
Check((await (await http.PostAsJsonAsync("/adminapi/login", new { username = "tester", password = "local-test" }))
    .Content.ReadFromJsonAsync<EpinelPS.Models.Admin.LoginApiResponse>())?.OK == true, "fixture login failed");
var html = await http.GetStringAsync($"/admin/Users/Modify/{target}");
var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
var path = $"/admin/Users/ResetLiberate/{target}";
Check(token.Length > 0 && Regex.IsMatch(html, $"<form\\b[^>]*action=\"{path}\""), "modify page has no independent liberation reset button");
Console.WriteLine("PASS: authenticated modify page exposes a separate liberation reset form");
Task<HttpResponseMessage> Post(ulong id) => http.PostAsync($"/admin/Users/ResetLiberate/{id}", new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
var otherSnapshot = JToken.FromObject(Current(other));
var before = JToken.FromObject(Current(target));
var anonymousCookies = new CookieContainer();
foreach (Cookie cookie in cookies.GetCookies(http.BaseAddress))
    if (cookie.Name != "token") anonymousCookies.Add(http.BaseAddress, new Cookie(cookie.Name, cookie.Value));
using var anonymous = new HttpClient(new HttpClientHandler { CookieContainer = anonymousCookies }) { BaseAddress = http.BaseAddress };
Check((await anonymous.PostAsync(path, new FormUrlEncodedContent([new("__RequestVerificationToken", token)]))).StatusCode == HttpStatusCode.Unauthorized,
    "unauthenticated request reset liberation");
Check((await http.PostAsync(path, null)).StatusCode == HttpStatusCode.BadRequest, "tokenless reset accepted");
Check((await http.GetAsync(path)).StatusCode == HttpStatusCode.MethodNotAllowed, "GET reset accepted");
Check((await Post(99999)).StatusCode == HttpStatusCode.NotFound, "unknown player accepted");
Check(JToken.DeepEquals(before, JToken.FromObject(Current(target))), "rejected request changed the player");
Console.WriteLine("PASS: authentication, forgery protection, POST-only and account checks prevent unintended resets");

Directory.CreateDirectory(save + ".tmp");
try
{
    Check((await Post(target)).StatusCode == HttpStatusCode.InternalServerError, "failed save reported success");
    Check(JToken.DeepEquals(before, JToken.FromObject(Current(target))), "failed save left a partial reset");
}
finally { Directory.Delete(save + ".tmp"); }
Console.WriteLine("PASS: a failed disk save rolls the reset back");

async Task Reset()
{
    var response = await Post(target);
    Check(response.IsSuccessStatusCode, "reset failed (HTTP " + (int)response.StatusCode + "): " + await response.Content.ReadAsStringAsync());
    var result = JObject.Parse(await response.Content.ReadAsStringAsync());
    Check(result.Value<int>("missionCount") == 3 && result.Value<int>("characterId") == character.Id, "reset returned an invalid batch");
}
var resetStart = DateTime.UtcNow.Ticks;
await Reset();
await Reset();
Reload();
var refreshed = Current(target).LiberateDatas[character.Id];
Check(refreshed.RewardedCount == 0 && refreshed.MissionData.Count == 3 &&
    refreshed.MissionData.All(m => m.MissionState == LiberateMissionState.Running && m.LiberateCharacterId == character.Id &&
        m.CreatedAt >= resetStart && m.TriggerStartAt >= resetStart && m.TriggerEndAt > m.TriggerStartAt &&
        data.LiberateMissionTable[m.MissionTid].GroupId == group.DefaultMissionGroupId), "reset did not create a fresh current-stage mission batch");
var expected = (JObject)before.DeepClone();
expected["LiberateDatas"]![character.Id.ToString()]!["RewardedCount"] = 0;
expected["LiberateDatas"]![character.Id.ToString()]!["MissionData"] = JToken.FromObject(refreshed.MissionData);
Check(JToken.DeepEquals(expected, JToken.FromObject(Current(target))), "reset changed progress, stage, another character, currency or daily missions");
Check(JToken.DeepEquals(otherSnapshot, JToken.FromObject(Current(other))), "reset changed another account");
var client = await Send<ResGetLiberateData>("/liberate/get", new ReqGetLiberateData());
Check(client.LiberateData.Equals(refreshed), "client read does not expose refreshed missions");
var progress = await Send<ResGetLiberateProgressList>("/liberate/getprogresslist", new ReqGetLiberateProgressList());
Check(progress.LiberateProgressData.Single(p => p.CharacterId == character.Id).ProgressPoint == 175,
    "client progress list changed accumulated points");
Console.WriteLine("PASS: repeated resets persist three running tasks, preserve earned progress and appear in client reads");

Task<ResGetLiberateData> blockedRead;
bool readWhileLocked;
using (var started = new ManualResetEventSlim())
{
    lock (Current(target))
    {
        blockedRead = Task.Run(async () => { started.Set(); return await Send<ResGetLiberateData>("/liberate/get", new ReqGetLiberateData()); });
        Check(started.Wait(TimeSpan.FromSeconds(5)), "reader task did not start");
        readWhileLocked = blockedRead.Wait(TimeSpan.FromMilliseconds(500));
    }
}
await blockedRead;
Check(!readWhileLocked, "lobby liberation reads bypass the reset's user lock and can observe a partially replaced mission list");
await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
{
    var reading = Send<ResGetLiberateData>("/liberate/get", new ReqGetLiberateData());
    await Reset();
    Check((await reading).LiberateData.MissionData.Count == 3, "overlapping read returned an incomplete mission packet");
}));
Console.WriteLine("PASS: a lobby read waits until the administrator's mission update is complete");

var pointBefore = refreshed.ProgressPoint;
var missionToComplete = refreshed.MissionData.First();
var expectedPoint = data.LiberateMissionTable[missionToComplete.MissionTid].MissionPointValue;
var complete = await Send<ResCompleteLiberateMission>("/liberate/completemission", new ReqCompleteLiberateMission { MissionId = missionToComplete.Id });
Check(complete.Data.ProgressPoint == pointBefore + expectedPoint && complete.Data.RewardedCount == 1,
    "new mission did not add its configured liberation points");
await Reset();
Check(Current(target).LiberateDatas[character.Id].ProgressPoint == pointBefore + expectedPoint, "a later reset erased earned points");
Console.WriteLine("PASS: a refreshed mission earns points and the next reset retains them");

var current = Current(target);
var curId = current.CurCharacterIdId;
current.CurCharacterIdId = 0;
Check((await Post(target)).StatusCode == HttpStatusCode.Conflict, "reset accepted no active character");
current.CurCharacterIdId = curId;
current.LiberateDatas[curId].IsCompleted = true;
Check((await Post(target)).StatusCode == HttpStatusCode.Conflict, "reset reopened a completed character");
current.LiberateDatas[curId].IsCompleted = false;
var originalStep = current.LiberateDatas[curId].Step;
current.LiberateDatas[curId].Step = 999;
Check((await Post(target)).StatusCode == HttpStatusCode.Conflict, "missing stage configuration accepted");
current.LiberateDatas[curId].Step = originalStep;
Console.WriteLine("PASS: absent, completed and unconfigured liberation states return clear failures");

current.LiberateDatas[curId].MissionData.Clear();
await Reset();
Check(current.LiberateDatas[curId].MissionData.Count == 3, "empty legacy mission list could not refill");
current.LiberateDatas[curId].ProgressPoint = 175;
await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Reset()));
Reload();
Check(Current(target).LiberateDatas[curId].RewardedCount == 0 && Current(target).LiberateDatas[curId].MissionData.Count == 3,
    "overlapping resets corrupted the batch");
Check(JToken.DeepEquals(otherSnapshot, JToken.FromObject(Current(other))), "overlapping resets changed another player");
Console.WriteLine("PASS: empty legacy lists and overlapping button requests remain usable");
Console.WriteLine("LiberateReset: 8 PASS, 0 FAIL");

async Task<T> Send<T>(string route, IMessage body) where T : IMessage<T>, new()
{
    using var scope = app.Services.CreateScope();
    var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    context.Items["UserID"] = target;
    context.Request.Path = "/v1" + route;
    context.Request.Body = new MemoryStream(body.ToByteArray());
    context.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(context);
    var result = new T();
    result.MergeFrom(((MemoryStream)context.Response.Body).ToArray());
    return result;
}
static void Check(bool ok, string reason)
{
    if (!ok) throw new InvalidOperationException(reason);
}

static async Task ProbeLive(string root)
{
    var runtime = Path.Combine(root, "Server and Server selector");
    var saved = JObject.Parse(File.ReadAllText(Path.Combine(runtime, "db.json")));
    ulong adminId;
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(runtime, "epinelps.db"), Mode = SqliteOpenMode.ReadOnly }.ToString()))
    {
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT ID FROM SdkUsers WHERE IsAdmin = 1 LIMIT 1";
        adminId = Convert.ToUInt64(command.ExecuteScalar() ?? throw new InvalidOperationException("No local administrator"));
    }
    string auth = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
        .WithKey(Convert.FromBase64String(saved.Value<string>("LauncherTokenKey")!), Encryption.SymmetricKey)
        .AddClaim("userId", adminId).IssuedAt(DateTime.UtcNow).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
    var cookies = new CookieContainer();
    var address = new Uri("https://127.0.0.1");
    cookies.Add(address, new Cookie("token", auth));
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false, CookieContainer = cookies, AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }) { BaseAddress = address };
    const ulong player = 1552239268;
    var html = await http.GetStringAsync($"/admin/Users/Modify/{player}");
    Check(Regex.IsMatch(html, $"<form\\b[^>]*action=\"/admin/Users/ResetLiberate/{player}\""), "live page has no liberation reset form");
    Check(html.Contains("id=\"liberateResetStatus\"") && html.Contains("users.modify.resetLiberate"), "live form has no status/button label");
    Check((await http.GetAsync($"/admin/Users/ResetLiberate/{player}")).StatusCode == HttpStatusCode.MethodNotAllowed,
        "live reset endpoint is absent or permits GET mutation");
    foreach (var language in new[] { "zh", "en", "ja", "ko" })
    {
        var translation = JObject.Parse(await http.GetStringAsync($"/admin/assets/i18n/{language}.json"));
        Check(!string.IsNullOrWhiteSpace(translation["users"]?["modify"]?.Value<string>("resetLiberate")), "live translation missing: " + language);
    }
    Console.WriteLine("PASS: live authenticated account page exposes the Liberation reset button, status and four translations");
    Console.WriteLine("PASS: live reset endpoint requires POST; no player reset was submitted");
}
