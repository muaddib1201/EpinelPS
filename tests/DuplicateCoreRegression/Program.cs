using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.LobbyServer.Controllers;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer.Gacha;
using EpinelPS.LobbyServer.Inventory;
using EpinelPS.LobbyServer.Shop;
using EpinelPS.Models;
using EpinelPS.Utils;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
var pack = Path.GetFullPath(Path.Combine(root, "../../Server and Server selector/cache/prdenv/153-cb882d4b30/staticdata/data/qa-261029-10b/578481/mpk/StaticData.pack"));
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
var progress = new Progress<double>();
foreach (var c in await data.LoadZip<CharacterRecord>("CharacterTable.json", progress)) data.CharacterTable[c.Id] = c;
foreach (var p in await data.LoadZip<ItemMaterialRecord>("ItemMaterialTable.json", progress)) data.itemMaterialTable[p.Id] = p;
foreach (var p in await data.LoadZip<OutpostBattleRecord>("OutpostBattleTable.json", progress)) data.OutpostBattle[p.Id] = p;
var officialShops = await data.LoadZip<ContentsShopRecord>("ContentsShopTable.json", progress);
var officialProducts = await data.LoadZip<ContentsShopProductRecord>("ContentsShopProductTable.json", progress);
var ssr = data.CharacterTable.Values.First(c => c.OriginalRare == OriginalRareType.SSR && c.GradeCoreId == 1 && c.PieceId > 0 && data.CharacterTable.Values.Any(v => v.NameCode == c.NameCode && v.GradeCoreId == 11));
var sr = data.CharacterTable.Values.First(c => c.OriginalRare == OriginalRareType.SR && c.GradeCoreId == 101 && c.PieceId > 0);
var rare = data.CharacterTable.Values.First(c => c.OriginalRare == OriginalRareType.R && c.GradeCoreId == 201);
Console.WriteLine($"Real-table fixtures: SSR {ssr.Id}, SR {sr.Id}, R {rare.Id}");
if (args.Contains("--audit-live-readonly"))
{
    var livePath = Path.GetFullPath(Path.Combine(root, "../../Server and Server selector/db.json"));
    var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(livePath))!;
    long cores = 0, labels = 0;
    foreach (var u in copy.Users)
    {
        long before = u.Items.Sum(i => (long)i.Count);
        labels += CharacterRewardHelper.ConvertExistingExcess(u);
        cores += before - u.Items.Sum(i => (long)i.Count);
    }
    Console.WriteLine($"READ-ONLY live inventory audit: {cores} unusable spare bodies -> {labels} Body Labels across {copy.Users.Count} account(s). No live files written.");
    return;
}
if (args.Contains("--verify-saved"))
{
    var saved = Newtonsoft.Json.JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db.json")))!.Users.Single();
    long before = saved.GetCurrencyVal(CurrencyType.DissolutionPoint);
    if (before <= 0 || CharacterRewardHelper.ConvertExistingExcess(saved) != 0 || saved.GetCurrencyVal(CurrencyType.DissolutionPoint) != before)
        throw new Exception("saved compensation was missing or reconciled twice");
    Console.WriteLine("PASS: saved character/inventory/currency state survives a fresh process without another compensation payment");
    return;
}
GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "duplicate-core-regression.db")}", "sqlite");
using (var db = GameContext.CreateNew()) { db.Database.EnsureDeleted(); db.Database.EnsureCreated(); }
var jsonType = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var instance = jsonType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
int passed = 0, failed = 0;

await Test("SSR core 7 converts normal recruit once", async () => {
    var u = Reset(ssr, 10); Banner(ssr);
    var r = await Recruit(u, 1);
    Equal(6000, Labels(u)); Equal(0, Bodies(u, ssr)); Equal(6000, r.Gacha.Single().CurrencyValue);
    Equal(u.Characters.Single().Tid, r.Characters.Single().Tid);
});
await Test("SR max and R duplicate compensation are not doubled", async () => {
    foreach (var pair in new[] { (sr, 2, 200), (rare, 0, 150) }) {
        var u = Reset(pair.Item1, pair.Item2); Banner(pair.Item1);
        var r = await Recruit(u, 1); Equal(pair.Item3, Labels(u)); Equal(pair.Item3, r.Gacha.Single().CurrencyValue);
    }
});
await Test("ten-pull crosses SSR cap using existing spare bodies", async () => {
    var u = Reset(ssr, 0, 9); Banner(ssr);
    var r = await Recruit(u, 10);
    Equal(10, Bodies(u, ssr)); Equal(54000, Labels(u)); Equal(1, r.Gacha.Sum(x => x.PieceCount));
    Equal(54000, r.Gacha.Sum(x => x.CurrencyValue));
});
await Test("table variant controls remaining upgrades despite stale saved Grade", () => {
    var u = Reset(ssr, 9); u.Characters.Single().Grade = 11;
    var r = Grant(u, ssr, 1); Equal(1, Bodies(u, ssr)); Equal(0, Labels(u)); Equal(1, r.Character.Single().PieceCount);
    return Task.CompletedTask;
});
await Test("SR second usable spare body is retained", () => {
    var u = Reset(sr, 1); var r = Grant(u, sr, 1);
    Equal(1, Bodies(u, sr)); Equal(0, Labels(u)); Equal(1, r.Character.Single().PieceCount); return Task.CompletedTask;
});
await Test("batch reward splits usable SSR body and excess labels", () => {
    var u = Reset(ssr, 9); var r = Grant(u, ssr, 3);
    Equal(1, Bodies(u, ssr)); Equal(12000, Labels(u)); Equal(1, r.Character.Sum(x => x.PieceCount)); Equal(12000, r.Currency.Sum(x => x.Value)); return Task.CompletedTask;
});
await Test("new character batch grants all copies with consistent serials", () => {
    var u = Reset(); var r = Grant(u, ssr, 3);
    Equal(1, u.Characters.Count); Equal(2, Bodies(u, ssr));
    if (r.Character.Any(c => c.Csn != u.Characters.Single().Csn)) throw new Exception("reward serial differs from owned serial");
    return Task.CompletedTask;
});
await Test("mold increments rather than replacing existing body stack", async () => {
    var u = Reset(ssr, 0, 3); Mold(ssr, u);
    await Send<ResUsePiece>(u, new UsePiece(), new ReqUsePiece { Isn = 777, Count = 1 });
    Equal(4, Bodies(u, ssr)); Equal(0, Labels(u));
});
await Test("mold at core 7 pays labels without an unusable body", async () => {
    var u = Reset(ssr, 10); Mold(ssr, u);
    var r = await Send<ResUsePiece>(u, new UsePiece(), new ReqUsePiece { Isn = 777, Count = 1 });
    Equal(0, Bodies(u, ssr)); Equal(6000, Labels(u)); Equal(6000, r.Reward.Currency.Sum(x => x.Value));
});
await Test("pity duplicate at max credits once and returns compensation", async () => {
    var u = Reset(ssr, 10); data.GachaPityRecords[1] = new() { Id = 1, NeedGachaCount = 1 };
    var r = await Send<ResExecuteGachaPity>(u, new ExecutePityGacha(), new ReqExecuteGachaPity { CharacterId = ssr.Id, GachaPityId = 1 });
    Equal(6000, Labels(u)); Equal(0, Bodies(u, ssr)); Equal(6000, r.Reward.Character.Sum(x => x.CurrencyValue));
});
await Test("pity usable body is reported after it is actually granted", async () => {
    var u = Reset(ssr, 9); data.GachaPityRecords[1] = new() { Id = 1, NeedGachaCount = 1 };
    var r = await Send<ResExecuteGachaPity>(u, new ExecutePityGacha(), new ReqExecuteGachaPity { CharacterId = ssr.Id, GachaPityId = 1 });
    Equal(1, Bodies(u, ssr)); Equal(1, r.Reward.Character.Sum(x => x.PieceCount)); Equal(0, Labels(u));
});
await Test("daily free duplicate uses compensation and cannot be claimed twice", async () => {
    var u = Reset(sr, 2); Banner(sr);
    var req = new ReqExecuteDailyFreeGacha { GachaId = 1, EventId = 123 };
    var r = await Send<ResExecuteDailyFreeGacha>(u, new ExecuteEventGacha(), req);
    Equal(200, Labels(u)); Equal(0, Bodies(u, sr)); Equal(200, r.Gacha.Single().CurrencyValue);
    try { await Send<ResExecuteDailyFreeGacha>(u, new ExecuteEventGacha(), req); } catch (InvalidOperationException) { }
    Equal(200, Labels(u));
});
await Test("first daily free character has no extra spare body", async () => {
    var u = Reset(); Banner(ssr, 124);
    var r = await Send<ResExecuteDailyFreeGacha>(u, new ExecuteEventGacha(), new ReqExecuteDailyFreeGacha { GachaId = 1, EventId = 124 });
    Equal(1, u.Characters.Count); Equal(0, Bodies(u, ssr)); Equal(0, r.Gacha.Single().PieceCount);
});
await Test("direct spare-body item converts excess but never unlocks unowned Nikke", () => {
    var u = Reset(ssr, 10); var r = new NetRewardData();
    RewardUtils.AddSingleObject(u, ref r, ssr.PieceId, RewardType.Item, 2);
    Equal(12000, Labels(u)); Equal(0, Bodies(u, ssr));
    u = Reset(); r = new(); RewardUtils.AddSingleObject(u, ref r, ssr.PieceId, RewardType.Item, 2);
    Equal(0, u.Characters.Count); Equal(2, Bodies(u, ssr)); Equal(0, Labels(u)); return Task.CompletedTask;
});
await Test("existing surplus reconciliation preserves needed bodies and is persistent/idempotent", () => {
    var u = Reset(ssr, 8, 4);
    u.Items.Add(new() { ItemType = ssr.PieceId, Count = 2, Isn = 113 });
    u.Items.Add(new() { ItemType = sr.PieceId, Count = 3, Isn = 114 }); // unowned: preserve
    var reconcile = typeof(RewardUtils).Assembly.GetType("EpinelPS.Utils.CharacterRewardHelper")?.GetMethod("ConvertExistingExcess")
        ?? throw new Exception("surplus reconciliation is missing");
    reconcile.Invoke(null, [u]); Equal(2, Bodies(u, ssr)); Equal(24000, Labels(u)); Equal(3, Bodies(u, sr));
    jsonType.GetMethod("Save")!.Invoke(null, null); jsonType.GetMethod("Reload")!.Invoke(null, null);
    u = ((CoreInfo)instance.GetValue(null)!).Users.Single(); reconcile.Invoke(null, [u]);
    Equal(24000, Labels(u)); Equal(2, Bodies(u, ssr)); return Task.CompletedTask;
});
await Test("single and bulk character purchases grant the same duplicate compensation", async () => {
    foreach (bool bulk in new[] { false, true }) {
        var u = Reset(ssr, 10); Shop(ssr.Id, RewardType.Character); u.Currency[CurrencyType.Gold] = 100;
        if (bulk) {
            var req = new ReqShopBuyMultipleProduct { ShopCategory = 1 };
            req.Products.Add(new NetBuyProductRequestData { ShopProductTid = 990001, Order = 1, Quantity = 1 });
            await Send<ResShopBuyMultipleProduct>(u, new BuyMultipleProduct(), req);
        } else await Send<ResShopBuyProduct>(u, new BuyProduct(), new ReqShopBuyProduct { ShopCategory = 1, ShopProductTid = 990001, Order = 1, Quantity = 1 });
        Equal(6000, Labels(u)); Equal(0, Bodies(u, ssr)); Equal(90, u.GetCurrencyVal(CurrencyType.Gold));
    }
});
await Test("single and bulk spare-body item purchases convert at max", async () => {
    foreach (bool bulk in new[] { false, true }) {
        var u = Reset(ssr, 10); Shop(ssr.PieceId, RewardType.Item); u.Currency[CurrencyType.Gold] = 100;
        if (bulk) { var q = new ReqShopBuyMultipleProduct { ShopCategory = 1 }; q.Products.Add(new NetBuyProductRequestData { ShopProductTid = 990001, Order = 1, Quantity = 2 }); await Send<ResShopBuyMultipleProduct>(u, new BuyMultipleProduct(), q); }
        else await Send<ResShopBuyProduct>(u, new BuyProduct(), new ReqShopBuyProduct { ShopCategory = 1, ShopProductTid = 990001, Order = 1, Quantity = 2 });
        Equal(12000, Labels(u)); Equal(0, Bodies(u, ssr)); Equal(80, u.GetCurrencyVal(CurrencyType.Gold));
    }
});
await Test("event shop character and spare-body purchases share duplicate handling", () => {
    foreach (bool bulk in new[] { false, true }) foreach (bool item in new[] { false, true }) {
        var u = Reset(ssr, 10); Shop(item ? ssr.PieceId : ssr.Id, item ? RewardType.Item : RewardType.Character); u.Currency[CurrencyType.Gold] = 100;
        if (bulk) {
            var q = new ReqEventShopMultipleBuyProduct { EventId = 123 };
            q.Products.Add(new NetBuyProductRequestData { ShopProductTid = 990001, Order = 1, Quantity = 1 });
            var r = new ResEventShopMultipleBuyProduct(); EpinelPS.LobbyServer.Event.Shop.EventShopHelper.BuyShopMultipleProduct(u, ref r, q);
            Equal(6000, r.Product.Currency.Sum(c => c.Value));
        } else {
            var r = new ResEventShopBuyProduct(); EpinelPS.LobbyServer.Event.Shop.EventShopHelper.BuyShopProduct(u, ref r,
                new ReqEventShopBuyProduct { EventId = 123, ShopProductTid = 990001, Order = 1, Quantity = 1 });
            Equal(6000, r.Product.Currency.Sum(c => c.Value));
        }
        Equal(6000, Labels(u)); Equal(0, Bodies(u, ssr)); Equal(90, u.GetCurrencyVal(CurrencyType.Gold));
    }
    return Task.CompletedTask;
});
await Test("first unlock settles unowned excess stock in the same response", () => {
    var u = Reset(); u.Items.Add(new() { ItemType = sr.PieceId, Isn = 115, Count = 4 });
    var r = Grant(u, sr, 1); Equal(2, Bodies(u, sr)); Equal(400, Labels(u));
    Equal(2, r.UserItems.Single(i => i.Isn == 115).Count); Equal(400, r.Currency.Sum(c => c.Value));
    return Task.CompletedTask;
});
await Test("merged character rewards include ownership and stable serials", () => {
    var u = Reset(); var one = Grant(u, sr, 1); var two = Grant(u, sr, 1);
    var merged = NetUtils.MergeRewards([one, two], u);
    Equal(2, merged.Character.Count); Equal(2, merged.UserCharacters.Count);
    if (merged.Character.Any(c => c.Csn != u.Characters.Single().Csn)) throw new Exception("merged serial mismatch");
    return Task.CompletedTask;
});
await Test("free recruit cannot use a fabricated event id", async () => {
    var u = Reset(sr, 2); Banner(sr);
    try { await Send<ResExecuteDailyFreeGacha>(u, new ExecuteEventGacha(), new ReqExecuteDailyFreeGacha { GachaId = 1, EventId = 999999 }); }
    catch (InvalidOperationException) { }
    Equal(0, Labels(u)); Equal(0, u.GachaDailyFreePulls.Count);
});
await Test("overlapping legacy reconciliation pays each stored core only once", async () => {
    var u = Reset(ssr, 10);
    for (int i = 0; i < 2048; i++) u.Items.Add(new() { ItemType = ssr.PieceId, Isn = 2000 + i, Count = 1 });
    var reconcile = typeof(RewardUtils).Assembly.GetType("EpinelPS.Utils.CharacterRewardHelper")!.GetMethod("ConvertExistingExcess")!;
    using var barrier = new Barrier(4);
    await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => { barrier.SignalAndWait(); reconcile.Invoke(null, [u]); })));
    Equal(2048L * 6000, Labels(u)); Equal(0, Bodies(u, ssr));
});
await Test("login snapshot settles old cores before returning inventory and currency", () => {
    var u = Reset(ssr, 10, 2);
    using var db = GameContext.CreateNew();
    var controller = new LobbyController(new TestUserService(u), db);
    var response = controller.EnterLobbyServer(new ReqEnterLobbyServer()).Value ?? throw new Exception("login did not return user data");
    Equal(12000, response.Currency.Single(c => c.Type == 9000).Value);
    Equal(0, response.Items.Where(i => i.Tid == ssr.PieceId).Sum(i => i.Count));
    controller.EnterLobbyServer(new ReqEnterLobbyServer()); Equal(12000, Labels(u));
    jsonType.GetMethod("Reload")!.Invoke(null, null);
    Equal(12000, Labels(((CoreInfo)instance.GetValue(null)!).Users.Single()));
    return Task.CompletedTask;
});
await Test("compensation buys a real Body Label Shop product", async () => {
    data.ContentsShopTable.Clear(); data.ContentsShopProductTable.Clear();
    foreach (var shop in officialShops) data.ContentsShopTable[shop.Id] = shop;
    foreach (var product in officialProducts) data.ContentsShopProductTable[product.Id] = product;
    var u = Reset(ssr, 10); Grant(u, ssr, 1);
    var realShop = officialShops.First(s => s.ShopType == ShopType.MainShop && officialProducts.Any(p => p.BundleId == s.BundleId && p.PriceType == PriceType.Currency && p.PriceId == 9000));
    var offers = NormalShopHelper.GetShopData(u, (int)realShop.ShopCategory);
    var offer = offers.List.First(o => data.ContentsShopProductTable[o.ProductId] is { PriceId: 9000, GoodsType: RewardType.Item, PriceValue: > 0 and <= 6000 });
    var item = data.ContentsShopProductTable[offer.ProductId];
    var response = await Send<ResShopBuyProduct>(u, new BuyProduct(), new ReqShopBuyProduct { ShopCategory = (int)realShop.ShopCategory, ShopProductTid = offer.ProductId, Order = offer.Order, Quantity = 1 });
    Equal(6000 - item.PriceValue, Labels(u));
    Equal(item.GoodsValue, u.Items.Where(i => i.ItemType == item.GoodsId).Sum(i => i.Count));
    Equal(Labels(u), response.Currencies.Last(c => c.Type == 9000).Value);
});
Console.WriteLine($"Duplicate core regression: {passed}/{passed + failed} passed");
Environment.ExitCode = failed == 0 ? 0 : 1;

User Reset(CharacterRecord? c = null, int grade = 0, int bodies = 0) {
    var u = new User { ID = 91001, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow };
    u.ResetableData.DailyCounselCount[1] = 0;
    u.Currency[CurrencyType.FreeCash] = 1000000;
    if (c != null) {
        var actual = data.CharacterTable.Values.First(v => v.NameCode == c.NameCode && v.GradeCoreId == c.GradeCoreId + grade);
        u.Characters.Add(new() { Csn = 101, Tid = actual.Id, Grade = grade, Level = 1 });
        if (bodies > 0) u.Items.Add(new() { ItemType = c.PieceId, Count = bodies, Isn = 112 });
    }
    instance.SetValue(null, new CoreInfo { Users = [u] });
    using var db = GameContext.CreateNew(); if (db.Users.Find(u.ID) == null) { db.Users.Add(new GameUser { ID = u.ID, Nickname = "Regression" }); db.SaveChanges(); }
    return u;
}
void Banner(CharacterRecord c, int eventId = 123) {
    data.gachaTypes.Clear(); data.GachaGradeProb.Clear(); data.GachaListProb.Clear();
    data.gachaTypes[1] = new() { Id = 1, Type = GachaPremiumType.GachaPremium, DailyFreeGachaEventId = eventId, GradeProbId = 990001, GachaPriceGroup = [new() { GachaPriceType = 99, GachaPriceValueCount1 = 300, GachaPriceValueCount10 = 3000 }] };
    data.GachaGradeProb[990001] = new() { Id = 990001, GroupId = 990001, Rare = c.OriginalRare, Prob = 100, GachaListId = 990001 };
    data.GachaListProb[990001] = new() { Id = 990001, GroupId = 990001, GachaType = GachaCategory.Character, GachaId = c.Id, Prob = 100 };
}
void Mold(CharacterRecord c, User u) { Banner(c); data.PieceItems[990002] = new() { Id = 990002, UseId = 990001 }; u.Items.Add(new() { ItemType = 990002, Count = 50, Isn = 777 }); }
void Shop(int id, RewardType type) {
    data.ContentsShopTable.Clear(); data.ContentsShopProductTable.Clear();
    data.ContentsShopTable[990001] = new() { Id = 990001, ShopType = ShopType.MainShop, ShopCategory = (ShopCategoryType)1, BundleId = 990001 };
    data.ContentsShopProductTable[990001] = new() { Id = 990001, BundleId = 990001, ProductOrder = 1, ProductProb = 100, GoodsId = id, GoodsType = type, GoodsValue = 1, PriceType = PriceType.Currency, PriceId = (int)CurrencyType.Gold, PriceValue = 10 };
}
NetRewardData Grant(User u, CharacterRecord c, int n) { var r = new NetRewardData(); RewardUtils.AddSingleObject(u, ref r, c.Id, RewardType.Character, n); return r; }
long Labels(User u) => u.GetCurrencyVal(CurrencyType.DissolutionPoint);
int Bodies(User u, CharacterRecord c) => u.Items.Where(i => i.ItemType == c.PieceId).Sum(i => i.Count);
Task<ResExecuteGacha> Recruit(User u, int n) => Send<ResExecuteGacha>(u, new ExecGacha(), new ReqExecuteGacha { Tid = 1, CurrencyType = (int)CurrencyType.FreeCash, Count = n });
void Equal(long expected, long actual) { if (expected != actual) throw new Exception($"Expected {expected}, got {actual}"); }
async Task Test(string name, Func<Task> test) { try { await test(); passed++; Console.WriteLine($"PASS: {name}"); } catch (Exception e) { failed++; Console.WriteLine($"FAIL: {name}: {e}"); } }
async Task<T> Send<T>(User u, LobbyMessage handler, IMessage req) where T : IMessage<T>, new() {
    using var services = new ServiceCollection().AddSingleton(new GameContext(new DbContextOptionsBuilder<GameContext>().Options)).BuildServiceProvider();
    var ctx = new DefaultHttpContext { RequestServices = services }; ctx.Items["UserID"] = u.ID;
    ctx.Request.Body = new MemoryStream(req.ToByteArray()); ctx.Response.Body = new MemoryStream();
    await handler.HandleAsync(ctx); var result = new T(); result.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray()); return result;
}

sealed class TestUserService(User user) : IUserService { public User? GetUser() => user; }
