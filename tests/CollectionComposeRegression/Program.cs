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

const ulong account = 1501, other = 1502;
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
string workspace = root?.FullName ?? throw new DirectoryNotFoundException();
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
foreach (var rarity in new[] { "R", "SR" })
{
    string prefix = "FavoriteItem" + rarity + "Compose";
    var id = int.Parse(data.GameSettings[prefix + "Reward"].Value!);
    var reward = data.GetRewardTableEntry(id);
    Console.WriteLine("RULE: " + JsonConvert.SerializeObject(new { rarity, need = data.GameSettings[prefix + "NeedCount"].Value, reward }));
    foreach (var r in reward.Rewards)
    {
        var item = data.ConsumableItems.GetValueOrDefault(r.RewardId);
        Console.WriteLine("REWARD_ITEM: " + JsonConvert.SerializeObject(item));
        if (item != null) Console.WriteLine("POOL: " + JsonConvert.SerializeObject(data.RandomItem.Values.Where(x => x.GroupId == item.UseId)));
    }
}
Console.WriteLine("COLLECTIONS: " + JsonConvert.SerializeObject(data.FavoriteItemTable.Values.Where(i => i.FavoriteType == FavoriteItemType.Collection).Select(i => new { i.Id, i.FavoriteRare, i.FavoriteType, i.MaxLevel })));

var db = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
string save = Path.Combine(AppContext.BaseDirectory, "db.json");
void Reload() => db.GetMethod("Reload")!.Invoke(null, null);
CoreInfo State() => (CoreInfo)db.GetProperty("Instance")!.GetValue(null)!;
User Current() => State().Users.Single(u => u.ID == account);
void Reset()
{
    var user = new User { ID = account, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow };
    for (int i = 0; i < 4; i++) user.FavoriteItems.Add(new NetUserFavoriteItemData { FavoriteItemId = 101 + i, Tid = 100101 + i * 100 });
    File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true,
        LauncherTokenKey = new byte[32], EncryptionTokenKey = new byte[32], Users = [user, new User { ID = other }] }));
    Reload();
}
Reset();
using var services = new ServiceCollection().AddSingleton(new GameContext(new DbContextOptionsBuilder<GameContext>().Options)).BuildServiceProvider();
try
{
    var reply = await Compose(101, 102, 103, 104);
    if (reply.Reward == null) throw new InvalidOperationException("four valid R collections returned no composition reward");
    if (Current().FavoriteItems.Any(i => i.FavoriteItemId is >= 101 and <= 104)) throw new InvalidOperationException("composition did not consume the four selected instances");
    Console.WriteLine("PASS: valid R composition returns a reward and consumes selected instances");
    return 0;
}
catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; }

async Task<ResComposeCollectionItem> Compose(params long[] ids)
{
    var req = new ReqComposeCollectionItem(); req.FavoriteItemId.AddRange(ids);
    var ctx = new DefaultHttpContext { RequestServices = services };
    ctx.Items["UserID"] = account; ctx.Request.Path = "/v1/favoriteitem/compose";
    ctx.Request.Body = new MemoryStream(req.ToByteArray()); ctx.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    return ResComposeCollectionItem.Parser.ParseFrom(((MemoryStream)ctx.Response.Body).ToArray());
}
