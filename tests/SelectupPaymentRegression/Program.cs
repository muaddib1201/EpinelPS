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
string connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "selectup-payment.db")}";
GameContext.InitializeOptions(connection, "sqlite");
using (var db = GameContext.CreateNew()) { db.Database.EnsureDeleted(); db.Database.EnsureCreated(); }
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
var jsonType = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var state = (CoreInfo)jsonType.GetProperty("Instance")!.GetValue(null)!;
var banner = data.gachaTypes[10094];
var choices = data.GachaSelectupListTable.Values.Where(c => c.GachaTypeId == banner.Id).OrderBy(c => c.Id).ToArray();
Check(choices.Length == 3, "fixture must cover all three current select-up choices");
int passed = 0, failed = 0;
ulong nextUser = 980000;
var initialWallets = new Dictionary<ulong, Dictionary<CurrencyType, long>>();
var grades = data.GachaGradeProb.Values.Where(g => g.GroupId == banner.GradeProbId).ToArray();
var original = grades.ToDictionary(g => g.Id, g => g.Prob);
var upGrade = grades.Single(g => data.GachaListProb.Values.Any(r => r.GroupId == g.GachaListId && r.GachaType == GachaCategory.GachaSelectup));
try
{
    // Exercise each selected-character grant deterministically, through real handlers.
    foreach (var g in grades) g.Prob = g.Id == upGrade.Id ? 10000 : 0;
    foreach (var choice in choices)
    foreach (int count in new[] { 1, 10 })
    foreach (int currency in new[] { 5503, 99 })
        await Test($"choice {choice.Id} count {count} currency {currency}: reward, payment, response and save", async () =>
        {
            var user = await NewUser(choice.Id, 10, 20, 100000, 10000);
            var response = await Pull(user, count, currency);
            Check(response.Gacha.Count == count && response.Gacha.All(c => c.Tid == choice.CharacterId), "selected character not delivered");
            Check(user.HasCharacter(choice.CharacterId), "selected character missing from inventory");
            CheckWallet(user, response, currency == 5503 ? 10 - count : 10, 20,
                currency == 99 ? 100000 - count * 300 : 100000, 10000);
            Check(user.GachaBannerMaxPulls[banner.Id] == count && user.GetCurrencyVal(CurrencyType.GoldMileageTicket) == count, "pull count or mileage missing");
            Check(response.Currencies.Single(c => c.Type == 12000).Value == count, "mileage response missing");
            using var db = GameContext.CreateNew();
            Check(db.Triggers.Where(t => t.UserId == user.ID && t.Type == Trigger.GachaCharacter).Sum(t => t.Value) == count, "wrong mission progress");
            CheckSaved(user);
        });
    foreach (var choice in choices)
        await Test($"choice {choice.Id}: select-up tickets then advanced tickets then gems", async () =>
        {
            var user = await NewUser(choice.Id, 2, 3, 1200, 1000);
            var response = await Pull(user, 10, 5503);
            CheckWallet(user, response, 0, 0, 0, 700);
            CheckSaved(user);
        });
    await Test("empty select-up wallet falls back to configured advanced tickets", async () =>
    {
        var user = await NewUser(10503, 0, 10, 0, 0);
        CheckWallet(user, await Pull(user, 10, 5503), 0, 0, 0, 0);
        CheckSaved(user);
    });
    await Test("advanced ticket request leaves select-up tickets available", async () =>
    {
        var user = await NewUser(10503, 10, 2, 2400, 0);
        CheckWallet(user, await Pull(user, 10, 5200), 10, 0, 0, 0);
    });
    await Test("empty ticket balances fall back to free then paid gems", async () =>
    {
        var user = await NewUser(10503, 0, 0, 299, 1);
        CheckWallet(user, await Pull(user, 1, 5503), 0, 0, 0, 0);
    });
    await Test("unconfigured advanced-ticket fallback is not consumed", async () =>
    {
        var price = banner.GachaPriceGroup.Single(p => p.GachaPriceType == 5200);
        int index = banner.GachaPriceGroup.IndexOf(price);
        banner.GachaPriceGroup.RemoveAt(index);
        try
        {
            var user = await NewUser(10503, 1, 10, 2700, 0);
            CheckWallet(user, await Pull(user, 10, 5503), 0, 10, 0, 0);
        }
        finally { banner.GachaPriceGroup.Insert(index, price); }
    });
    await Test("insufficient mixed payment rolls back all rewards and debits", async () =>
    {
        var user = await NewUser(10503, 2, 3, 1499, 0);
        await Rejected(user, () => Pull(user, 10, 5503));
    });
    await Test("empty wallet cannot recruit for free", async () =>
    {
        var user = await NewUser(10503, 0, 0, 0, 0);
        await Rejected(user, () => Pull(user, 1, 5503));
    });
    await Test("select-up ticket cannot pay on a standard banner", async () =>
    {
        var user = await NewUser(10503, 10, 10, 100000, 10000);
        await Rejected(user, () => Pull(user, 10, 5503, 1));
    });
    await Test("foreign select-up ticket cannot pay on this banner", async () =>
    {
        var user = await NewUser(10503, 10, 10, 100000, 10000);
        user.Currency[CurrencyType.CharSelect01Ticket] = 10;
        jsonType.GetMethod("Save")!.Invoke(null, null);
        await Rejected(user, () => Pull(user, 10, 5501));
    });
    foreach (int currency in new[] { 5503, 99 })
        await Test($"unsupported discount with currency {currency} cannot make recruitment free", async () =>
        {
            var user = await NewUser(10503, 0, 0, 100000, 10000);
            await Rejected(user, () => Pull(user, 1, currency, discount: true));
        });
    await Test("two simultaneous ten-pulls cannot spend the same ten tickets", async () =>
    {
        var user = await NewUser(10503, 10, 0, 0, 0);
        async Task<bool> Attempt()
        {
            try { await Pull(user, 10, 5503); return true; }
            catch (BadHttpRequestException ex) when (ex.StatusCode == 400) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(Attempt), Task.Run(Attempt));
        Check(results.Count(v => v) == 1, "expected one success and one insufficient-balance rejection");
        Check(user.GetCurrencyVal(CurrencyType.CharSelect03Ticket) == 0 && user.GachaBannerMaxPulls[10094] == 10,
            "duplicate spend or pull-count corruption");
        Check(user.GetCurrencyVal(CurrencyType.GoldMileageTicket) == 10, "duplicate mileage");
        CheckSaved(user);
    });
}
finally { foreach (var g in grades) g.Prob = original[g.Id]; Console.SetOut(output); }
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

async Task<User> NewUser(int choice, long tickets, long advanced, long gems, long paid)
{
    var user = new User { ID = ++nextUser, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow };
    user.Currency[CurrencyType.CharSelect03Ticket] = tickets;
    user.Currency[CurrencyType.CharCustomizeTicket] = advanced;
    user.Currency[CurrencyType.FreeCash] = gems;
    user.Currency[CurrencyType.ChargeCash] = paid;
    initialWallets[user.ID] = new(user.Currency);
    state.Users.Add(user);
    using (var db = GameContext.CreateNew())
    { db.Users.Add(new GameUser { ID = user.ID, Nickname = "Synthetic recruit test" }); db.SaveChanges(); }
    await Send<ResSetGachaSelectup>(new SetGachaSelectSetup(), user.ID, new ReqSetGachaSelectup
    { GachaSelectupData = new() { GachaTypeId = 10094, GachaSelectupId = choice } });
    return user;
}
Task<ResExecuteGacha> Pull(User user, int count, int currency, int bannerId = 10094, bool discount = false) =>
    Send<ResExecuteGacha>(new ExecGacha(), user.ID, new ReqExecuteGacha
    { Tid = bannerId, Count = count, CurrencyType = currency, IsDiscount = discount });
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
void CheckWallet(User user, ResExecuteGacha response, long tickets, long advanced, long gems, long paid)
{
    foreach (var (type, expected) in new[] { (5503, tickets), (5200, advanced), (99, gems), (98, paid) })
    {
        Check(user.GetCurrencyVal((CurrencyType)type) == expected, $"wrong wallet balance for {type}");
        var returned = response.Currencies.Where(c => c.Type == type).ToArray();
        Check(returned.Length <= 1 && returned.All(c => c.Value == expected), $"wrong response balance for {type}");
        if (initialWallets[user.ID].GetValueOrDefault((CurrencyType)type) != expected)
            Check(returned.Length == 1, $"response omitted changed currency {type}");
    }
    // Every payment response must include the resource actually charged, including its zero balance.
    Check(response.Currencies.Any(c => c.Type is 5503 or 5200 or 99 or 98), "payment response omitted all debits");
}
void CheckSaved(User user)
{
    var persisted = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))!.Users.Single(u => u.ID == user.ID);
    Check(JToken.DeepEquals(JObject.FromObject(user), JObject.FromObject(persisted)), "saved account differs from successful response state");
}
async Task Rejected(User user, Func<Task<ResExecuteGacha>> action)
{
    var before = JObject.FromObject(user); string disk = File.ReadAllText(save);
    int count;
    using (var db = GameContext.CreateNew()) count = db.Triggers.Count(t => t.UserId == user.ID);
    bool rejected = false;
    try { await action(); }
    catch (BadHttpRequestException ex) when (ex.StatusCode == 400) { rejected = true; }
    Check(rejected, "invalid or unaffordable payment was accepted");
    Check(JToken.DeepEquals(before, JObject.FromObject(user)), "failed request mutated account");
    Check(disk == File.ReadAllText(save), "failed request changed persisted save");
    using var after = GameContext.CreateNew();
    Check(count == after.Triggers.Count(t => t.UserId == user.ID), "failed request published mission progress");
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; output.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; output.WriteLine("FAIL " + name + ": " + ex); }
}
static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
