using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.Models;
using EpinelPS.Utils;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

// Static data and every save used here belong to this session's workspace.
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
string workspace = root?.FullName ?? throw new DirectoryNotFoundException();
string pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var db = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var dbProperty = db.GetProperty("Instance")!;
var save = db.GetMethod("Save")!;
var reload = db.GetMethod("Reload")!;
User Current() => ((CoreInfo)dbProperty.GetValue(null)!).Users.Single();
using var services = new ServiceCollection()
    .AddSingleton(new GameContext(new DbContextOptionsBuilder<GameContext>().Options)).BuildServiceProvider();
int passed = 0;
List<string> failures = [];

foreach (var (tid, position) in new[] { (3110701, 0), (3210701, 1), (3310701, 2), (3410701, 3) })
    await Run($"slot {position}: exchange response immediately clears the previous item in client inventory", async () =>
    {
        Reset(tid, position);
        var client = ClientInventory();
        var response = await Wear(202);
        Apply(client, response);
        Check(client[101].Csn == 0 && client[101].Position == 0, "previous equipment was not cleared by response");
        Check(client[202].Csn == 1001 && client[202].Position == position, "replacement was not equipped by response");
        Check(client.Values.Count(i => i.Csn == 1001 && i.Position == position) == 1, "client still has duplicate equipment in the slot");
        Check(response.Items.Count(i => i.Isn == 101) == 1 && response.Items.Count(i => i.Isn == 202) == 1, "response must contain one final delta per changed item");
        Check(client[303].Csn == 1002, "another character's equipment changed");
        Persisted();
    });

await Run("exchange splits one equipment from a stack and preserves its metadata and total count", async () =>
{
    Reset(3110701, 0, count: 2);
    var client = ClientInventory();
    var response = await Wear(202);
    Apply(client, response);
    Check(client[202].Count == 1 && client[202].Csn == 0, "remaining stack must stay in inventory with one item");
    var equipped = client.Values.Where(i => i.Csn == 1001).ToArray();
    Check(equipped.Length == 1 && equipped[0].Isn != 202 && equipped[0].Count == 1, "equipped item must have its own instance ID and count one");
    Check(equipped[0].Tid == 3110701 && equipped[0].Corporation == 7 && equipped[0].Lv == 3 && equipped[0].Exp == 800, "split lost equipment metadata");
    Check(Current().Items.Sum(i => i.Count) == 4 && client.Values.Sum(i => i.Count) == 4, "exchange changed the total number of items");
    Check(Current().Items.Count == 4 && response.Items.Select(i => i.Isn).Distinct().Count() == response.Items.Count, "split did not create exactly one distinct instance");
    Persisted();
});

await Run("transferring equipment updates both characters through the same response", async () =>
{
    Reset(3110701, 0);
    Current().Items.Single(i => i.Isn == 202).Csn = 1002;
    Current().Items.Single(i => i.Isn == 303).Csn = 0;
    var client = ClientInventory();
    Apply(client, await Wear(202));
    Check(client[101].Csn == 0 && client[202].Csn == 1001, "target character retained its previous equipment");
    Check(!client.Values.Any(i => i.Csn == 1002), "source character still owns transferred equipment");
    Persisted();
});

await Run("selecting an already equipped item returns only its final state", async () =>
{
    Reset(3210701, 1);
    var response = await Wear(101);
    Check(response.Items.Count == 1 && response.Items[0].Isn == 101 && response.Items[0].Csn == 1001 && response.Items[0].Position == 1,
        "reselecting returned intermediate or duplicate equipment state");
    Check(Current().Items.Count == 3 && Current().Items.Sum(i => i.Count) == 3, "reselecting duplicated equipment");
    Persisted();
});

foreach (var (isn, csn, count, name) in new[]
{
    (999L, 1001L, 1, "missing item"),
    (202L, 9999L, 1, "unowned character"),
    (202L, 0L, 1, "zero character ID"),
    (202L, 1001L, 0, "empty stack")
})
    await Run($"{name}: invalid exchange cannot silently unequip or modify inventory", async () =>
    {
        Reset(3110701, 0, count);
        string before = Snapshot();
        bool rejected = false;
        try { await Wear(isn, csn); }
        catch (BadHttpRequestException) { rejected = true; }
        Check(Snapshot() == before, "invalid request mutated the inventory");
        Check(rejected, "invalid exchange returned a successful response");
    });

if (failures.Count > 0) throw new Exception($"FAIL: {string.Join("; ", failures)}");
Console.WriteLine($"PASS: {passed} equipment exchange scenarios");

void Reset(int tid, int position, int count = 1)
{
    var user = new User
    {
        ID = 990001, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow,
        Characters = [new CharacterModel { Csn = 1001 }, new CharacterModel { Csn = 1002 }],
        Items =
        [
            new DbItemData { Isn = 101, ItemType = tid, Count = 1, Csn = 1001, Position = position, Corp = 2 },
            new DbItemData { Isn = 202, ItemType = tid, Count = count, Corp = 7, Level = 3, Exp = 800 },
            new DbItemData { Isn = 303, ItemType = tid, Count = 1, Csn = 1002, Position = position }
        ]
    };
    dbProperty.SetValue(null, new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true, Users = [user] });
    save.Invoke(null, null);
}
string Snapshot() => JsonConvert.SerializeObject(Current());
Dictionary<long, NetUserItemData> ClientInventory() => Current().Items.Select(NetUtils.ToNet).ToDictionary(i => i.Isn);
void Apply(Dictionary<long, NetUserItemData> client, ResWearEquipment response)
{
    foreach (var item in response.Items) client[item.Isn] = item.Clone();
}
void Persisted()
{
    string expected = Snapshot();
    reload.Invoke(null, null);
    Check(Snapshot() == expected, "server equipment did not survive save/reload");
}
async Task<ResWearEquipment> Wear(long isn, long csn = 1001)
{
    var context = new DefaultHttpContext { RequestServices = services };
    context.Items["UserID"] = 990001UL;
    context.Request.Path = "/v1/inventory/wearequipment";
    context.Request.Body = new MemoryStream(new ReqWearEquipment { Isn = isn, Csn = csn }.ToByteArray());
    context.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(context);
    return ResWearEquipment.Parser.ParseFrom(((MemoryStream)context.Response.Body).ToArray());
}
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
async Task Run(string name, Func<Task> check)
{
    try { await check(); passed++; Console.WriteLine($"PASS: {name}"); }
    catch (Exception e) { failures.Add($"{name}: {e.Message}"); Console.WriteLine($"FAIL: {name}: {e.Message}"); }
}
