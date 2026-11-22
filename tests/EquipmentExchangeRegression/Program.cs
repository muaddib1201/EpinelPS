using LockRequest = EpinelPS.AwakeningOptionLockReserveRequest;
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

await Run("single-instance awakened equipment keeps its option association", async () =>
{
    Reset(3111001, 0);
    Current().EquipmentAwakenings.Add(new EquipmentAwakeningData
    {
        Isn = 202, Option = new NetEquipmentAwakeningOption { Option1Id = 12345, Option1Lock = true }
    });
    string options = JsonConvert.SerializeObject(Current().EquipmentAwakenings);
    var client = ClientInventory();
    Apply(client, await Wear(202));
    Check(client[101].Csn == 0 && client[202].Csn == 1001 && client[202].Count == 1, "awakened item changed identity or old ownership remained");
    Check(JsonConvert.SerializeObject(Current().EquipmentAwakenings) == options, "awakening options changed during equip");
    Persisted();
});

foreach (bool alreadyEquipped in new[] { false, true })
    await Run($"legacy awakened stack (equipped={alreadyEquipped}) is rejected without losing or duplicating options", async () =>
    {
        Reset(3111001, 0, count: 2);
        if (alreadyEquipped)
        {
            Current().Items.Single(i => i.Isn == 101).Csn = 0;
            Current().Items.Single(i => i.Isn == 202).Csn = 1001;
        }
        Current().EquipmentAwakenings.Add(new EquipmentAwakeningData
        {
            Isn = 202, Option = new NetEquipmentAwakeningOption { Option1Id = 12345, Option1Lock = true }
        });
        string before = Snapshot();
        bool rejected = false;
        try { await Wear(202); }
        catch (BadHttpRequestException) { rejected = true; }
        Check(Snapshot() == before, "ambiguous awakened stack lost its original item/option association");
        Check(rejected, "ambiguous awakened stack was silently split");
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

const int chargeEffect = 7000904, critEffect = 7001204; // Screenshot: 6.88% and 9.58%.
const int module = 7080001, key = 7080002;
foreach (bool valuesOnly in new[] { false, true })
foreach (bool accept in new[] { false, true })
    await Run($"one-shot slot 2: valuesOnly={valuesOnly}, accept={accept}, reconnect and retry", async () =>
    {
        PrepareRoll();
        var result = await RollSecond(valuesOnly);
        Check(result.Option.Option2Id == critEffect, "one-shot lock lost the exact 9.58% effect ID");
        Check(!result.Option.Option2Lock && !result.Option.IsOption2DisposableLock, "one-shot lock survived its roll");
        var critGroup = data.EquipmentOptionTable.Values.Where(r => r.StateEffectList.Any(s => s.StateEffectId == critEffect))
            .Select(r => r.StateEffectGroupId).First();
        var critIds = data.EquipmentOptionTable.Values.Where(r => r.StateEffectGroupId == critGroup)
            .SelectMany(r => r.StateEffectList).Select(s => s.StateEffectId).ToHashSet();
        Check(!critIds.Contains(result.Option.Option1Id) && !critIds.Contains(result.Option.Option3Id),
            "an unlocked slot duplicated the locked effect group");
        Check(Balance(module) == 8 && Balance(key) == 80 && result.Items.Count == 2, "wrong material charge or missing client deltas");
        Persisted();
        var detail = await Send("equipment/getawakeningdetail", new ReqGetAwakeningDetail { Isn = 202 }, ResGetAwakeningDetail.Parser);
        Check(detail.NewOption.Equals(result.Option) && detail.CurrentOption.Option2Id == critEffect, "reconnect lost the pending comparison");
        var retry = await RollSecond(valuesOnly);
        Check(result.Option.Equals(retry.Option) && Balance(module) == 8 && Balance(key) == 80, "retry rerolled or charged twice");
        var chosen = await Choose(accept);
        Check(chosen.Awakening.Option.Equals(accept ? result.Option : detail.CurrentOption), "keep/apply chose the wrong options");
        Persisted();
        Check(Current().EquipmentAwakenings.Count == 1 && Current().EquipmentAwakenings[0].Option.Option2Id == critEffect,
            "saved equipment lost the selected effect");
        await Send("equipment/upgradeoption", new ReqAwakeningUpgradeOption { Isn = 202 }, ResAwakeningUpgradeOption.Parser);
        Check(Balance(module) == 7 && Balance(key) == 80, "one-shot lock incorrectly charged on the next roll");
    });

await Run("permanent and disposable reservations protect two slots at their configured rates", async () =>
{
    PrepareRoll();
    var r = await Send("equipment/resetoption", new ReqAwakeningResetOption
    {
        Isn = 202, Slot1 = LockRequest.ReservePermanentLock, Slot2 = LockRequest.ReserveDisposableLock
    }, ResAwakeningResetOption.Parser);
    Check(r.ResetOption.Option1Id == chargeEffect && r.ResetOption.Option1Lock &&
        r.ResetOption.Option2Id == critEffect && !r.ResetOption.Option2Lock, "mixed lock types did not preserve the chosen slots");
    Check(Balance(module) == 5 && Balance(key) == 70, "mixed lock pricing should be 2+3 modules and 30 keys");
    await Choose(false);
    var next = await Send("equipment/resetoption", new ReqAwakeningResetOption { Isn = 202 }, ResAwakeningResetOption.Parser);
    Check(next.ResetOption.Option1Lock && next.ResetOption.Option1Id == chargeEffect &&
        Balance(module) == 3 && Balance(key) == 70, "existing permanent lock was lost or charged a second setup fee");
});

await Run("two disposable locks use successive key prices and aggregate split stacks", async () =>
{
    PrepareRoll();
    Current().Items.Single(x => x.ItemType == key).Count = 10;
    Current().Items.Add(new DbItemData { Isn = 606, ItemType = key, Count = 40 });
    var r = await Send("equipment/resetoption", new ReqAwakeningResetOption
    {
        Isn = 202, Slot1 = LockRequest.ReserveDisposableLock, Slot2 = LockRequest.ReserveDisposableLock
    }, ResAwakeningResetOption.Parser);
    Check(r.ResetOption.Option1Id == chargeEffect && r.ResetOption.Option2Id == critEffect, "multiple protected slots changed");
    Check(Balance(key) == 0 && Balance(module) == 7 && r.Items.Count(x => x.Count == 0 && x.Tid == key) == 2,
        "20+30 keys or split stack deltas were wrong");
});

foreach (bool shortOfKeys in new[] { true, false })
    await Run($"insufficient {(shortOfKeys ? "keys" : "modules")} leaves options and both materials intact", async () =>
    {
        PrepareRoll();
        Current().Items.Single(x => x.ItemType == (shortOfKeys ? key : module)).Count = shortOfKeys ? 19 : 1;
        string before = Snapshot();
        var result = await RollSecond(false);
        Check(result.Option == null && result.Items.Count == 0 && Snapshot() == before, "partial charge or pending candidate on refusal");
    });

await Run("old one-shot flags on slot 3 protect the first roll and then clear", async () =>
{
    PrepareRoll();
    var option = Current().EquipmentAwakenings.Single().Option;
    option.Option3Id = critEffect;
    option.Option2Id = 0;
    option.IsOption3DisposableLock = true;
    var result = await Send("equipment/resetoption", new ReqAwakeningResetOption { Isn = 202 }, ResAwakeningResetOption.Parser);
    Check(result.ResetOption.Option3Id == critEffect && !result.ResetOption.IsOption3DisposableLock, "legacy lock rerolled");
    await Choose(false);
    Check(!Current().EquipmentAwakenings.Single().Option.IsOption3DisposableLock, "keep retained a spent legacy lock");
});

await Run("reserve on an empty slot cannot consume currency or roll the equipment", async () =>
{
    PrepareRoll();
    string before = Snapshot();
    var r = await Send("equipment/upgradeoption", new ReqAwakeningUpgradeOption { Isn = 202, Slot3 = LockRequest.ReserveDisposableLock },
        ResAwakeningUpgradeOption.Parser);
    Check(r.ResetOption == null && Snapshot() == before, "empty lock request mutated state");
});

await Run("effect change uses the global tier bands and preserves a one-shot lock", async () =>
{
    PrepareRoll();
    var state = (CoreInfo)dbProperty.GetValue(null)!;
    Check(state.EffectChangeBands == null, "a legacy save should use the static data");
    state.EffectChangeBands = new EffectChangeBands { LowEnd = 1, MiddleEnd = 14, LowPercent = 0, MiddlePercent = 0, HighPercent = 100 };
    save.Invoke(null, null);
    reload.Invoke(null, null);
    var response = await RollSecond(false);
    var maximumIds = data.EquipmentOptionTable.Values.Where(x => x.EquipmentOptionGroupId == 100000)
        .SelectMany(x => x.StateEffectList).Where(x => x.StateEffectLevel == 15).Select(x => x.StateEffectId).ToHashSet();
    Check(maximumIds.Contains(response.Option.Option1Id) && response.Option.Option2Id == critEffect &&
        (response.Option.Option3Id == 0 || maximumIds.Contains(response.Option.Option3Id)), "custom bands or the protected slot were ignored");
    await Choose(false);
});

await Run("all probability tickets follow custom boundaries including zero and fractional rates", () =>
{
    var bands = new EffectChangeBands { LowEnd = 2, MiddleEnd = 13, LowPercent = 0.01m, MiddlePercent = 49.99m, HighPercent = 50m };
    for (int ticket = 0; ticket < 10000; ticket++)
    {
        int first = bands.DrawLevel(new BandTicket(ticket, false));
        int last = bands.DrawLevel(new BandTicket(ticket, true));
        var expected = ticket == 0 ? (1, 2) : ticket < 5000 ? (3, 13) : (14, 15);
        Check((first, last) == expected, "wrong tier boundaries at ticket " + ticket);
    }
    return Task.CompletedTask;
});

if (failures.Count > 0) throw new Exception($"FAIL: {string.Join("; ", failures)}");
Console.WriteLine($"PASS: {passed} equipment scenarios");

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
void PrepareRoll()
{
    Reset(3111001, 0);
    var user = Current();
    user.EquipmentAwakenings.Add(new EquipmentAwakeningData
    {
        Isn = 202, Option = new NetEquipmentAwakeningOption { Option1Id = chargeEffect, Option2Id = critEffect }
    });
    user.Items.Add(new DbItemData { Isn = 404, ItemType = module, Count = 10 });
    user.Items.Add(new DbItemData { Isn = 505, ItemType = key, Count = 100 });
}
int Balance(int tid) => Current().Items.Where(x => x.ItemType == tid).Sum(x => x.Count);
Task<ResAwakeningChangeOption> Choose(bool accept) => Send("equipment/changeoption",
    new ReqAwakeningChangeOption { Isn = 202, IsChanged = accept }, ResAwakeningChangeOption.Parser);
async Task<(NetEquipmentAwakeningOption Option, IList<NetUserItemData> Items)> RollSecond(bool valuesOnly)
{
    if (valuesOnly)
    {
        var r = await Send("equipment/upgradeoption", new ReqAwakeningUpgradeOption { Isn = 202, Slot2 = LockRequest.ReserveDisposableLock },
            ResAwakeningUpgradeOption.Parser);
        return (r.ResetOption, r.Items);
    }
    var reset = await Send("equipment/resetoption", new ReqAwakeningResetOption { Isn = 202, Slot2 = LockRequest.ReserveDisposableLock },
        ResAwakeningResetOption.Parser);
    return (reset.ResetOption, reset.Items);
}

Task<ResWearEquipment> Wear(long isn, long csn = 1001) => Send("wearequipment",
    new ReqWearEquipment { Isn = isn, Csn = csn }, ResWearEquipment.Parser);
async Task<T> Send<T>(string operation, IMessage request, MessageParser<T> parser) where T : IMessage<T>
{
    var context = new DefaultHttpContext { RequestServices = services };
    context.Items["UserID"] = 990001UL;
    context.Request.Path = "/v1/inventory/" + operation;
    context.Request.Body = new MemoryStream(request.ToByteArray());
    context.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(context);
    return parser.ParseFrom(((MemoryStream)context.Response.Body).ToArray());
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

sealed class BandTicket(int ticket, bool upper) : Random
{
    public override int Next(int maxValue) => ticket;
    public override int Next(int minValue, int maxValue) => upper ? maxValue - 1 : minValue;
}
