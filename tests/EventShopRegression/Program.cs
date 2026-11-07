using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Event.Shop;
using EpinelPS.Models;
using EpinelPS.Utils;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

var cache = Path.Combine(AppContext.BaseDirectory, "../../../../../../../../Server and Server selector/cache");
var pack = new Uri(GameConfig.Root.StaticDataMpk.Url).AbsolutePath.TrimStart('/');
var data = new GameData(Path.Combine(cache, pack));
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
var progress = new Progress<double>();
foreach (var row in await data.LoadZip<EventManagerRecord>("EventManagerTable.json", progress)) data.eventManagers.Add(row.Id, row);
foreach (var row in await data.LoadZip<ContentsShopRecord>("ContentsShopTable.json", progress)) data.ContentsShopTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<ContentsShopProductRecord>("ContentsShopProductTable.json", progress)) data.ContentsShopProductTable.Add(row.Id, row);

var user = new User { ID = 1 };
typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb", true)!.GetProperty("Instance")!
    .SetValue(null, new CoreInfo { Users = [user] });
GameContext.InitializeOptions("Data Source=:memory:", "sqlite");
using var db = GameContext.CreateNew();
using var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();

var events = data.eventManagers.Values.Where(e => e.EventSystemType == EventSystemType.ShopEvent).ToList();
if (events.Count == 0 || data.ContentsShopTable.Count == 0 || data.ContentsShopProductTable.Count == 0)
    throw new Exception("Missing shop fixtures");
var failures = new List<string>();
int mapped = 0;
foreach (var entry in events)
{
    if (!int.TryParse(entry.EventShortcutId, out int id) || id <= 0 || !data.ContentsShopTable.TryGetValue(id, out var expected))
    {
        await ExpectEmpty(entry.Id, $"event {entry.Id} without a mapped shop");
        continue;
    }
    var products = data.ContentsShopProductTable.Values.Where(p => p.BundleId == expected.BundleId).Select(p => p.Id).Order();
    var shop = await ListShop(entry.Id);
    if (shop.ShopTid != expected.Id || shop.ShopCategory != (int)expected.ShopCategory ||
        !shop.List.Select(p => p.ProductId).Order().SequenceEqual(products))
        throw new Exception($"Event {entry.Id} returned the wrong shop or products");
    mapped++;
    if (entry.Id == 83502) Console.WriteLine($"Event {entry.Id} -> shop {shop.ShopTid}, {shop.List.Count} products");
}
Console.WriteLine($"PASS: {mapped} mapped event shops returned their own product lists");

// Reproduce an absent target with another shop that matches only the event ID's suffix.
var fixture = new EventManagerRecord { Id = 80104, EventSystemType = EventSystemType.ShopEvent, EventShortcutId = "9000301" };
data.eventManagers[fixture.Id] = fixture;
data.ContentsShopTable.Clear();
data.ContentsShopTable.Add(9010401, new() { Id = 9010401, BundleId = 90104 });
await ExpectEmpty(fixture.Id, "missing mapped shop with a matching bundle suffix");

// Even an exact shop key equal to the event ID cannot substitute for the shortcut.
data.ContentsShopTable.Add(fixture.Id, new() { Id = fixture.Id, BundleId = 90104 });
foreach (var shortcut in new string?[] { "9000301", null, "", "shop", "2147483648", "0", "-1" })
{
    fixture.EventShortcutId = shortcut;
    await ExpectEmpty(fixture.Id, $"unresolved shortcut '{shortcut ?? "<null>"}' with event-ID collision");
}
if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine($"FAIL: {failure}");
    Environment.ExitCode = 1;
    return;
}
Console.WriteLine("PASS: missing and invalid mappings returned an empty event shop");

async Task ExpectEmpty(int eventId, string scenario)
{
    var shop = await ListShop(eventId);
    if (shop.ShopTid != 0 || shop.List.Count != 0)
        failures.Add($"{scenario}: returned shop {shop.ShopTid} with {shop.List.Count} products");
}

async Task<NetEventShopProductData> ListShop(int eventId)
{
    var context = new DefaultHttpContext { RequestServices = services };
    context.Items["UserID"] = user.ID;
    using var request = new MemoryStream(new ReqEventShopProductList { EventId = eventId }.ToByteArray());
    using var response = new MemoryStream();
    context.Request.Body = request;
    context.Response.Body = response;
    await new ProductList().HandleAsync(context);
    return ResEventShopProductList.Parser.ParseFrom(response.ToArray()).Shops[0];
}
