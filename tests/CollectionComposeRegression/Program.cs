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
using Newtonsoft.Json.Linq;

const ulong account = 1501, other = 1502;
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
string workspace = root?.FullName ?? throw new DirectoryNotFoundException();
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var db = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
string save = Path.Combine(AppContext.BaseDirectory, "db.json");
void Reload() => db.GetMethod("Reload")!.Invoke(null, null);
CoreInfo State() => (CoreInfo)db.GetProperty("Instance")!.GetValue(null)!;
User Current() => State().Users.Single(u => u.ID == account);
string Snapshot() => JsonConvert.SerializeObject(State());
long[] R(int count = 4) => Enumerable.Range(101, count).Select(i => (long)i).ToArray();
long[] SR(int count = 3) => Enumerable.Range(201, count).Select(i => (long)i).ToArray();
void Reset()
{
    var user = new User { ID = account, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow };
    for (int i = 0; i < 8; i++) user.FavoriteItems.Add(new NetUserFavoriteItemData { FavoriteItemId = 101 + i, Tid = 100101 + i % 6 * 100 });
    for (int i = 0; i < 6; i++) user.FavoriteItems.Add(new NetUserFavoriteItemData { FavoriteItemId = 201 + i, Tid = 100102 + i * 100 });
    user.FavoriteItems.Add(new NetUserFavoriteItemData { FavoriteItemId = 301, Tid = 200101 });
    user.FavoriteItems.Add(new NetUserFavoriteItemData { FavoriteItemId = 401, Tid = 100101, Csn = 777 });
    user.FavoriteItems.Add(new NetUserFavoriteItemData { FavoriteItemId = 402, Tid = 100101, Lv = 1 });
    user.FavoriteItems.Add(new NetUserFavoriteItemData { FavoriteItemId = 403, Tid = 100101, Exp = 1 });
    user.Items.Add(new DbItemData { Isn = 900, ItemType = 7150002, Count = 7 });
    user.Items.Add(new DbItemData { Isn = 901, ItemType = 7150003, Count = 11 });
    user.Items.Add(new DbItemData { Isn = 902, ItemType = 9210003, Count = 3 });
    user.Currency[CurrencyType.Gold] = 777; user.Currency[CurrencyType.FreeCash] = 123;
    var foreign = new User { ID = other };
    foreign.FavoriteItems.Add(new NetUserFavoriteItemData { FavoriteItemId = 501, Tid = 100101 });
    File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true,
        LauncherTokenKey = new byte[32], EncryptionTokenKey = new byte[32], Users = [user, foreign] }));
    Reload();
}
Reset();
using var services = new ServiceCollection().AddSingleton(new GameContext(new DbContextOptionsBuilder<GameContext>().Options)).BuildServiceProvider();
int passes = 0, failures = 0;
foreach (int tid in new[] { 100102, 100202, 100302, 100402, 100502, 100602 })
{
    await Run($"R composition awards configured SR collection {tid} in both client reward fields", async () =>
    {
        using var pool = ForceReward(tid);
        string unrelated = Unrelated();
        var retained = Current().FavoriteItems.Where(i => !R().Contains(i.FavoriteItemId)).ToArray();
        var reply = await Compose(R());
        var reward = reply.Reward ?? throw new Exception("four valid R collections returned no composition reward");
        Check(reward.FavoriteItems.Count == 1 && reward.UserFavoriteItems.Count == 1 && reward.Item.Count == 0, "missing SR reward representation");
        var item = reward.UserFavoriteItems.Single();
        Check(item.Tid == tid && item.Csn == 0 && item.Lv == 0 && item.Exp == 0 && item.FavoriteItemId > 0, "invalid SR instance");
        Check(reward.FavoriteItems.Single().FavoriteItemId == item.FavoriteItemId && reward.FavoriteItems.Single().Tid == tid, "reward instance IDs disagree");
        Check(Current().FavoriteItems.Take(retained.Length).SequenceEqual(retained) && !Current().FavoriteItems.Any(i => R().Contains(i.FavoriteItemId)), "wrong materials removed or unrelated instances changed");
        Check(Current().FavoriteItems.Last().Equals(item) && Unrelated() == unrelated, "response not registered or unrelated state changed");
        Reload();
        Check(Current().FavoriteItems.Count == retained.Length + 1 && Current().FavoriteItems.Any(i => i.Equals(item)), "result did not persist");
        await Reject(R());
    });
}
foreach (var (tid, delta, final) in new[] { (7150003, 10, 21), (7150002, 20, 27) })
    await Run($"R maintenance reward {tid} reports gained and final stack counts", async () =>
    {
        using var pool = ForceReward(tid);
        var reply = await Compose(R());
        Check(reply.Reward?.Item.Single().Tid == tid && reply.Reward.Item.Single().Count == delta, "wrong maintenance reward");
        Check(reply.Reward.UserItems.Single().Count == final && Current().Items.Single(i => i.ItemType == tid).Count == final, "incorrect stack snapshot");
        Reload(); Check(Current().Items.Single(i => i.ItemType == tid).Count == final, "maintenance reward not persisted");
    });
await Run("SR consumes three and awards the configured maintenance selection box", async () =>
{
    var reply = await Compose(SR());
    Check(reply.Reward?.Item.Single().Tid == 9210003 && reply.Reward.Item.Single().Count == 1, "wrong SR reward");
    Check(reply.Reward.UserItems.Single().Count == 4 && !Current().FavoriteItems.Any(i => SR().Contains(i.FavoriteItemId)), "SR inputs or box stack incorrect");
});
await Run("batch R draws grant distinct SR IDs while leaving equipped and enhanced collections", async () =>
{
    using var pool = ForceReward(100102);
    var reply = await Compose(R(8));
    var ids = reply.Reward.UserFavoriteItems.Select(i => i.FavoriteItemId).ToArray();
    Check(ids.Length == 2 && ids.Distinct().Count() == 2 && ids.All(i => i > 0 && i is not (>= 101 and <= 108)), "batch SR IDs collide");
    Check(reply.Reward.FavoriteItems.Select(i => i.FavoriteItemId).SequenceEqual(ids), "batch reward fields disagree");
    Check(Current().FavoriteItems.Count(i => i.FavoriteItemId is >= 401 and <= 403) == 3, "protected collections removed");
});
await Run("batch R maintenance rewards are aggregated with one final stack snapshot", async () =>
{
    using var pool = ForceReward(7150002);
    var reply = await Compose(R(8));
    Check(reply.Reward.Item.Count == 1 && reply.Reward.Item.Single().Count == 40, "batch gained counts not combined");
    Check(reply.Reward.UserItems.Count == 1 && reply.Reward.UserItems.Single().Count == 47, "batch returned stale or duplicate stack snapshots");
});
await Run("batch SR gives one selection box per three selected collections", async () =>
{
    var reply = await Compose(SR(6));
    Check(reply.Reward?.Item.Single().Tid == 9210003 && reply.Reward.Item.Single().Count == 2 && reply.Reward.UserItems.Single().Count == 5, "incorrect SR batch result");
});
await Run("new maintenance stacks are registered and persist through reload", async () =>
{
    using var pool = ForceReward(7150002);
    Current().Items.Clear();
    var reply = await Compose(R());
    var gained = reply.Reward.UserItems.Single();
    Reload();
    Check(gained.Isn > 0 && Current().Items.Single().Isn == gained.Isn && Current().Items.Single().Count == 20, "new stack was not persisted with its instance ID");
});

foreach (var (name, ids) in new (string, long[])[] {
    ("empty", []), ("too few R", [101,102,103]), ("incomplete R batch", [101,102,103,104,105]),
    ("too few SR", [201,202]), ("mixed rarity", [101,102,103,201]), ("repeated instance", [101,102,103,103]),
    ("missing instance", [101,102,103,999]), ("foreign instance", [101,102,103,501]),
    ("equipped collection", [101,102,103,401]), ("enhanced collection", [101,102,103,402]),
    ("nonzero experience", [101,102,103,403]), ("favorite treasure", [101,102,103,301]),
    ("zero instance", [101,102,103,0]), ("negative instance", [101,102,103,-1]) })
    await Run("rejects " + name + " without changing inventory or save", () => Reject(ids));
await Run("rejects an unknown definition", async () => { Current().FavoriteItems[0].Tid = int.MaxValue; await Reject(R()); });
await Run("rejects ambiguous stored instance IDs", async () => { Current().FavoriteItems.Add(Current().FavoriteItems[0].Clone()); await Reject(R()); });
await Run("another account cannot consume this account's collections", async () => { await Reject(R(), other); });

await Run("concurrent retries consume one group and award exactly once", async () =>
{
    using var pool = ForceReward(7150002);
    var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
    {
        try { return await Compose(R()); } catch (BadHttpRequestException) { return null; }
    })));
    Check(results.Count(r => r?.Reward != null) == 1 && Current().Items.Single(i => i.ItemType == 7150002).Count == 27, "retry duplicated reward or removed extra materials");
    Reload(); await Reject(R());
});
await Run("concurrent requests for disjoint groups preserve both results", async () =>
{
    using var pool = ForceReward(7150002);
    var results = await Task.WhenAll(Task.Run(() => Compose(R())), Task.Run(() => Compose([105,106,107,108])));
    Check(results.All(r => r.Reward != null) && Current().Items.Single(i => i.ItemType == 7150002).Count == 47, "concurrent composition lost or duplicated a reward");
});

foreach (string rarity in new[] { "R", "SR" })
    await Run(rarity + " composition reads the configured material count", async () =>
    {
        var setting = data.GameSettings["FavoriteItem" + rarity + "ComposeNeedCount"];
        string? previous = setting.Value;
        try { setting.Value = "2"; Check((await Compose(rarity == "R" ? R(2) : SR(2))).Reward != null, "material count was hardcoded"); }
        finally { setting.Value = previous; }
    });
foreach (string invalid in new[] { "0", "-1", "bad", "999999999999999999999" })
    await Run("invalid configured material count " + invalid + " cannot consume anything", async () =>
    {
        var setting = data.GameSettings["FavoriteItemRComposeNeedCount"]; string? previous = setting.Value;
        try { setting.Value = invalid; await Reject(R(), configuration: true); } finally { setting.Value = previous; }
    });
await Run("missing reward configuration cannot consume anything", async () =>
{
    var row = data.GameSettings["FavoriteItemRComposeReward"]; data.GameSettings.Remove(row.Id!);
    try { await Reject(R(), configuration: true); } finally { data.GameSettings.Add(row.Id!, row); }
});
await Run("an empty configured probability pool cannot consume anything", async () =>
{
    var rows = data.RandomItem.Values.Where(i => i.GroupId == 9490101).ToArray();
    foreach (var row in rows) data.RandomItem.Remove(row.Id);
    try { await Reject(R(), configuration: true); } finally { foreach (var row in rows) data.RandomItem.Add(row.Id, row); }
});
await Run("an invalid configured reward is rejected before removal", async () =>
{
    var row = data.RandomItem.Values.First(i => i.GroupId == 9490101); int id = row.RewardId;
    try { row.RewardId = int.MaxValue; await Reject(R(), configuration: true); } finally { row.RewardId = id; }
});
await Run("a material stack overflow cannot consume collections", async () =>
{
    using var pool = ForceReward(7150002); Current().Items.Single(i => i.ItemType == 7150002).Count = int.MaxValue;
    await Reject(R(), configuration: true);
});
foreach (int tid in new[] { 100102, 7150002, 9210003 })
    await Run($"failed save rolls back {tid} including existing object identities and permits retry", async () =>
    {
        using var pool = tid == 9210003 ? null : ForceReward(tid);
        var favoriteList = Current().FavoriteItems; var itemList = Current().Items;
        var favorites = favoriteList.ToArray(); var items = itemList.ToArray();
        string memory = Snapshot(), disk = File.ReadAllText(save);
        Directory.CreateDirectory(save + ".tmp"); bool failed = false;
        try { await Compose(tid == 9210003 ? SR(6) : R(8)); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
        finally { Directory.Delete(save + ".tmp"); }
        Check(failed && Snapshot() == memory && File.ReadAllText(save) == disk, "save failure retained changes or reported success");
        Check(ReferenceEquals(Current().FavoriteItems, favoriteList) && ReferenceEquals(Current().Items, itemList) &&
            favorites.Where((f, i) => !ReferenceEquals(f, favoriteList[i])).Count() == 0 &&
            items.Where((f, i) => !ReferenceEquals(f, itemList[i])).Count() == 0, "rollback replaced existing inventory objects");
        Check((await Compose(tid == 9210003 ? SR(6) : R(8))).Reward != null, "retry after failed persistence lost the materials");
    });

Console.WriteLine($"RESULT: {passes} passed, {failures} failed");
return failures == 0 ? 0 : 1;

Task<ResComposeCollectionItem> Compose(params long[] ids) => ComposeAs(account, ids);
async Task<ResComposeCollectionItem> ComposeAs(ulong user, long[] ids)
{
    var req = new ReqComposeCollectionItem(); req.FavoriteItemId.AddRange(ids);
    var ctx = new DefaultHttpContext { RequestServices = services };
    ctx.Items["UserID"] = user; ctx.Request.Path = "/v1/favoriteitem/compose";
    ctx.Request.Body = new MemoryStream(req.ToByteArray()); ctx.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    return ResComposeCollectionItem.Parser.ParseFrom(((MemoryStream)ctx.Response.Body).ToArray());
}
async Task Reject(long[] ids, ulong user = account, bool configuration = false)
{
    string memory = Snapshot(), disk = File.ReadAllText(save); bool rejected = false;
    try { await ComposeAs(user, ids); }
    catch (BadHttpRequestException e) when (e.StatusCode is >= 400 and < 500) { rejected = true; }
    catch (Exception e) when (configuration && e is InvalidDataException or OverflowException) { rejected = true; }
    Check(rejected, "invalid composition was acknowledged as success");
    Check(Snapshot() == memory && File.ReadAllText(save) == disk, "rejected composition changed account state");
}
IDisposable ForceReward(int tid)
{
    var rows = data.RandomItem.Values.Where(i => i.GroupId == 9490101).ToArray();
    var before = rows.Select(i => i.Ratio).ToArray();
    foreach (var row in rows) row.Ratio = row.RewardId == tid ? 10000 : 0;
    return new Cleanup(() => { for (int i = 0; i < rows.Length; i++) rows[i].Ratio = before[i]; });
}
string Unrelated()
{
    var state = JObject.FromObject(State());
    foreach (var user in state["Users"]!) { ((JObject)user).Remove("FavoriteItems"); ((JObject)user).Remove("Items"); }
    return state.ToString(Formatting.None);
}
async Task Run(string name, Func<Task> test)
{
    try { Reset(); await test(); passes++; Console.WriteLine("PASS: " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL: " + name + " — " + e.Message); }
}
static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }
