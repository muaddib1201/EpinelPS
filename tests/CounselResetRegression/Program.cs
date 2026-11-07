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

const ulong target = 1001, other = 1002;
const int character = 5185;
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
foreach (var row in await data.LoadZip<InfraCoreGradeRecord>("InfraCoreGradeTable.json", new Progress<double>()))
    data.InfracoreTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<DispatchBoardRecord>("DispatchBoardTable.json", new Progress<double>()))
    data.DispatchBoardTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<AttractiveLevelRecord>("AttractiveLevelTable.json", new Progress<double>()))
    data.AttractiveLevelTable.Add(row.Id, row);
var first = new User { ID = target, InfraCoreLvl = 17 };
first.ResetableData.DailyCounselCount[1] = 0;
first.ResetableData.DailyCounselCount[42] = 7;
first.ResetableData.DailyMissionPoints = 40;
first.ResetableData.DispatchCount = 3;
first.Currency[CurrencyType.FreeCash] = 125;
first.BondInfo.Add(new NetUserAttractiveData { NameCode = character, Lv = 2, Exp = 340, CounseledCount = 25,
    CanCounselToday = true, IsFavorites = true, CounselDialogCompleteIds = { 3386 }, ObtainedRewardLevels = { 1 } });
var second = new User { ID = other, InfraCoreLvl = 1 }; // An older save with no daily counter entry.
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true, Users = [first, second] }));
var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
User Current(ulong id) => ((CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!).Users.Single(u => u.ID == id);
void Reload() => jsonDb.GetMethod("Reload")!.Invoke(null, null);

const string counselConnection = "Data Source=counsel-reset-regression;Mode=Memory;Cache=Shared";
GameContext.InitializeOptions(counselConnection, "sqlite");
using var sqlite = new SqliteConnection(counselConnection);
sqlite.Open();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(UsersController).Assembly.GetName().Name });
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(sqlite));
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
var login = await http.PostAsJsonAsync("/adminapi/login", new { username = "tester", password = "local-test" });
Check((await login.Content.ReadFromJsonAsync<EpinelPS.Models.Admin.LoginApiResponse>())?.OK == true, "fixture login failed");
var html = await http.GetStringAsync($"/admin/Users/Modify/{target}");
var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
Check(token.Length > 0, "admin page did not issue a forgery token");
var path = $"/admin/Users/ResetCounsel/{target}";
Check(Regex.IsMatch(html, $"<form\\b[^>]*action=\"{path}\""), "modify page has no independent counsel reset form");
Console.WriteLine("PASS: authenticated modify page renders the separate reset form");

Task<HttpResponseMessage> Post(ulong id) => http.PostAsync($"/admin/Users/ResetCounsel/{id}",
    new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
async Task Reset(ulong id, int expect)
{
    var response = await Post(id);
    response.EnsureSuccessStatusCode();
    Check(JObject.Parse(await response.Content.ReadAsStringAsync()).Value<int>("remaining") == expect, "unexpected refill amount");
}
var anonymousCookies = new CookieContainer();
foreach (Cookie cookie in cookies.GetCookies(http.BaseAddress))
    if (cookie.Name != "token") anonymousCookies.Add(http.BaseAddress, new Cookie(cookie.Name, cookie.Value));
using var anonymous = new HttpClient(new HttpClientHandler { CookieContainer = anonymousCookies }) { BaseAddress = http.BaseAddress };
Check((await anonymous.PostAsync(path, new FormUrlEncodedContent([new("__RequestVerificationToken", token)]))).StatusCode ==
    HttpStatusCode.Unauthorized, "session without admin credentials accepted");
Check((await http.PostAsync(path, null)).StatusCode == HttpStatusCode.BadRequest, "missing anti-forgery token accepted");
Check((await http.GetAsync(path)).StatusCode == HttpStatusCode.MethodNotAllowed, "GET reset accepted");
Check((await Post(9999)).StatusCode == HttpStatusCode.NotFound, "missing account accepted");
Check(Current(target).ResetableData.DailyCounselCount[1] == 0, "rejected request mutated target");
Console.WriteLine("PASS: unauthorized, tokenless, GET and unknown-user requests cannot reset a player");

var otherSnapshot = JToken.FromObject(Current(other));
var before = JToken.FromObject(Current(target));
// Block only this isolated save's atomic temp file to simulate a failed disk write.
Directory.CreateDirectory(save + ".tmp");
try
{
    Check((await Post(target)).StatusCode == HttpStatusCode.InternalServerError, "failed save reported success");
    Check(JToken.DeepEquals(before, JToken.FromObject(Current(target))), "failed save left a partial refill in memory");
}
finally { Directory.Delete(save + ".tmp"); }
Console.WriteLine("PASS: a failed save rolls back the refill and can be retried");
await Reset(target, 9);
await Reset(target, 9);
Reload();
var after = JToken.FromObject(Current(target));
var expectedDaily = (JObject)before["ResetableData"]!;
expectedDaily["DailyCounselCount"]!["1"] = 9;
expectedDaily["CounselRefilledToday"] = true;
Check(JToken.DeepEquals(before, after), "reset changed fields outside this player's counsel allowance");
Check(JToken.DeepEquals(otherSnapshot, JToken.FromObject(Current(other))), "reset affected the other account");
Check(await Available(target) == 9 && LobbyHandler.CreateNetUserDataFromUser(Current(target)).CounselCount == 9, "client reads did not return 9");
Console.WriteLine("PASS: repeated reset persists 9/9 and preserves all other account and character fields");

await Send<ResCharacterCounsel>(target, "/character/attractive/counsel", new ReqCharacterCounsel { NameCode = character, CounselTid = 3391 });
Check(await Available(target) == 8, "normal counsel did not consume one");
await Send<ResCharacterQuickCounsel>(target, "/character/counsel/quick", new ReqCharacterQuickCounsel { NameCode = character });
Reload();
Check(await Available(target) == 7 && LobbyHandler.CreateNetUserDataFromUser(Current(target)).CounselCount == 7, "quick counsel/relogin did not preserve 7");
Check(Current(target).BondInfo.Single().CounselDialogCompleteIds.SequenceEqual([3386, 3391]), "answer journal changed unexpectedly");
await Task.WhenAll(Enumerable.Range(0, 7).Select(_ => Task.Run(() =>
    Send<ResCharacterQuickCounsel>(target, "/character/counsel/quick", new ReqCharacterQuickCounsel { NameCode = character }))));
Check(await Available(target) == 0 && Current(target).BondInfo.Single().CounseledCount == 34, "concurrent consultations lost a use");
await Reset(target, 9);
Check(await Available(target) == 9, "empty account did not refill");
Console.WriteLine("PASS: normal and quick counsel consume once, persist across reload and refill from zero");

await Task.WhenAll(Reset(target, 9), Send<ResCharacterQuickCounsel>(target, "/character/counsel/quick", new ReqCharacterQuickCounsel { NameCode = character }));
Reload();
Check((await Available(target)) is 8 or 9 && Current(target).BondInfo.Single().CounseledCount == 35, "overlapping refill lost counseling progress");
Console.WriteLine("PASS: an overlapping refill and counsel retain the character's progress");

await Send<ResCharacterQuickCounsel>(other, "/character/counsel/quick", new ReqCharacterQuickCounsel { NameCode = character });
Check(await Available(other) == 3, "an account that never clicked the button changed behavior");
await Reset(other, 3);
Check(await Available(other) == 3, "lower-level account exceeded its limit");
Current(other).InfraCoreLvl = 999;
Check((await Post(other)).StatusCode == HttpStatusCode.Conflict && await Available(other) == 3, "missing grade changed the allowance");
Current(target).LastWeeklyReset = DateTime.UtcNow;
Current(target).LastReset = DateTime.MinValue;
Current(target).ResetDataIfNeeded();
await Send<ResCharacterQuickCounsel>(target, "/character/counsel/quick", new ReqCharacterQuickCounsel { NameCode = character });
Check(await Available(target) == 9, "normal daily rollover retained yesterday's refill mode");
Console.WriteLine("PASS: lower-level and legacy saves use their own limit, and the normal daily rollover still works");

async Task<int> Available(ulong id) =>
    (await Send<ResGetAttractiveList>(id, "/character/attractive/get", new ReqGetAttractiveList())).CounselAvailableCount;
async Task<T> Send<T>(ulong id, string route, IMessage body) where T : IMessage<T>, new()
{
    using var scope = app.Services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Items["UserID"] = id;
    ctx.Request.Path = "/v1" + route;
    ctx.Request.Body = new MemoryStream(body.ToByteArray());
    ctx.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    var result = new T();
    result.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray());
    return result;
}
static void Check(bool ok, string reason)
{
    if (!ok) throw new InvalidOperationException(reason);
}
