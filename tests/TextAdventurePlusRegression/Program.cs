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

var root = Environment.GetEnvironmentVariable("NIKKE_WORKSPACE_ROOT")
    ?? throw new Exception("Use the isolated NIKKE test workflow");
var savePath = Path.Combine(AppContext.BaseDirectory, "db.json");
var pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
var progress = new Progress<double>();
var managers = await data.LoadZip<EventTextAdvPlusManagerRecord_Raw>("EventTextAdvPlusManagerTable.json", progress);
var routes = await data.LoadZip<EventTextAdvPlusRouteRecord_Raw>("EventTextAdvPlusRouteTable.json", progress);
var pages = await data.LoadZip<EventTextAdvPlusEventPageRecord_Raw>("EventTextAdvPlusEventPageTable.json", progress);
var rewards = await data.LoadZip<EventTextAdvPlusProgressRewardRecord_Raw>("EventTextAdvPlusProgressRewardTable.json", progress);
var firstSelects = await data.LoadZip<EventTextAdvPlusFirstSelectRecord_Raw>("EventTextAdvPlusFirstSelectTable.json", progress);
var options = await data.LoadZip<EventTextAdvPlusSelectOptionRecord_Raw>("EventTextAdvPlusSelectOptionTable.json", progress);
var items = await data.LoadZip<EventTextAdvPlusLootItemRecord_Raw>("EventTextAdvPlusLootItemTable.json", progress);
var intros = await data.LoadZip<EventTextAdvPlusSpecialIntroRecord_Raw>("EventTextAdvPlusSpecialIntroTable.json", progress);
Populate("EventTextAdvPlusManagerTable", managers);
Populate("EventTextAdvPlusRouteTable", routes);
Populate("EventTextAdvPlusEventPageTable", pages);
Populate("EventTextAdvPlusProgressRewardTable", rewards);
Populate("EventTextAdvPlusFirstSelectTable", firstSelects);
Populate("EventTextAdvPlusSelectOptionTable", options);
Populate("EventTextAdvPlusLootItemTable", items);
Populate("EventTextAdvPlusSpecialIntroTable", intros);
foreach (var row in await data.LoadZip<RewardRecord>("RewardTable.json", progress)) data.RewardDataRecords.Add(row.Id, row);
foreach (var row in await data.LoadZip<JukeboxListRecord>("JukeboxListTable.json", progress)) data.jukeboxListDataRecords.Add(row.Id, row);
if (managers.Length == 0 || routes.Length == 0 || pages.Length == 0) throw new Exception("Missing TextAdventurePlus static fixtures");
Console.WriteLine($"Loaded {managers.Length} managers, {routes.Length} routes, {pages.Length} pages and {rewards.Length} progress rewards");

var user = new User { ID = 1 };
typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb", true)!.GetProperty("Instance")!
    .SetValue(null, new CoreInfo { Users = [user], BondRewardLevelsMigrated = true });
GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "text-adventure-fixture.sqlite")}", "sqlite");
using var db = GameContext.CreateNew();
db.Database.EnsureDeleted();
db.Database.EnsureCreated();
db.Users.Add(new GameUser { ID = user.ID });
db.SaveChanges();
using var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
var manager = managers.Single(m => m.Id == 83501);
foreach (var route in new[] { "Enter", "Enter/Route", "Finish", "Reward/Progress", "Reward/Ending", "Reward/Check", "Route/Check", "Route/Badge/Remove", "LootItem/Badge/Remove", "Ending/Badge/Remove", "Log/Select", "Log/Interrupt" })
{
    bool unauthorized = false;
    try { await Call<ResGetNow>("/MiniGame/TextAdventurePlus/" + route, new ReqGetNow(), authenticated: false); }
    catch (BadHttpRequestException e) when (e.StatusCode == 401) { unauthorized = true; }
    Assert(unauthorized, $"{route} must return HTTP 401 for an unauthenticated request");
}
Console.WriteLine("PASS: all twelve minigame endpoints return HTTP 401 without authentication");
var entered = await Call<ResEnterMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Enter",
    new ReqEnterMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = manager.Id });
if (entered.PlayData == null) throw new Exception("FAIL: clicking the minigame returns no PlayData and leaves the client waiting");
if (entered.PlayData.PlayCount != 0 || entered.ReceivedEventTextAdvPlusProgressRewardIdList.Count != 0 || entered.ReceivedEventTextAdvPlusEventPageTableIdList.Count != 0)
    throw new Exception("New players must start without completed games or claimed rewards");
Console.WriteLine("PASS: minigame entry returns initialized player data");
Assert(!(await CheckReward()).IsRewardExist, "Fresh users must not have claimable rewards");
Assert((await Call<ResCheckNewRouteMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Route/Check", new ReqCheckNewRouteMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = 83501 })).IsNewRouteExist, "Unlocked unseen routes must be signalled");
await Reject(() => Call<ResEnterMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Enter", new ReqEnterMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = 0 }));

var noClaim = await ClaimProgress(31);
Assert(noClaim.ReceivedEventTextAdvPlusProgressRewardIdList.Count == 0 && noClaim.Reward.Currency.Count == 0, "Rewards must require a discovered ending");
var inlineFinish = Route11Ending(6110, 113701, 1109, restart: true);
var unopened = await Finish(inlineFinish);
Assert(unopened.PlayData.PlayCount == 0 && unopened.PlayData.EventTextAdvPlusEventPageTableIdList.Count == 0 &&
    unopened.PlayData.AcquiredEventTextAdvPlusLootItemTableIdList.Count == 0, "Restarting must not open a route the player has never entered");
await OpenRoute(11);
var finish = new ReqFinishMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = 83501, EventTextAdvPlusRouteTableId = 11, EventTextAdvPlusFirstSelectTableId = firstSelects.First(s => s.RouteId == 11).Id };
finish.EventTextAdvPlusEventPageTableIdList.AddRange([6101, 6101]);
var loot = items.First(i => i.ManagerId == 83501).Id;
finish.AcquiredEventTextAdvPlusLootItemTableIdList.AddRange([loot, loot]);
finish.CheckedEventTextAdvPlusLootItemTableIdList.Add(loot);
var finished = await Call<ResFinishMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Finish", finish);
Assert(finished.PlayData.PlayCount == 1 && finished.PlayData.EventTextAdvPlusEventPageTableIdList.SequenceEqual([6101]) && finished.PlayData.AcquiredEventTextAdvPlusLootItemTableIdList.SequenceEqual([loot]), "Finished progress must merge distinct records");
var retry = await Call<ResFinishMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Finish", finish);
Assert(retry.PlayData.PlayCount == 1, "Retrying a finished route must not count another play");
Assert((await CheckReward()).IsRewardExist, "One of 15 endings must unlock the 6 percent reward");
var claim = await ClaimProgress(31, 31, 32, -1);
Assert(claim.ReceivedEventTextAdvPlusProgressRewardIdList.SequenceEqual([31]) && claim.Reward.CalculateSize() > 0, "Only the eligible distinct progress reward may be paid");
var claimAgain = await ClaimProgress(31);
Assert(claimAgain.Reward.Currency.Count == 0 && claimAgain.Reward.Item.Count == 0, "Claim retries must not pay again");
Console.WriteLine("PASS: route completion, percentage rewards and retries preserve progress without duplicate payments");

await OpenRoute(11);
var invalid = finish.Clone();
invalid.EventTextAdvPlusEventPageTableIdList.Add(6201);
await Reject(() => Call<ResFinishMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Finish", invalid));
var unchanged = await Enter();
Assert(unchanged.PlayData.PlayCount == 1 && !unchanged.PlayData.EventTextAdvPlusEventPageTableIdList.Contains(6201), "Cross-route finish must not partially mutate progress");
finish.EventTextAdvPlusEventPageTableIdList.Clear();
finish.EventTextAdvPlusEventPageTableIdList.Add(8101);
await Call<ResFinishMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Finish", finish);
var endingRequest = new ReqClaimMiniGameTextAdventurePlusEndingAlbumReward { EventTextAdvPlusManagerTableId = 83501 };
endingRequest.EventTextAdvPlusEventPageTableIdList.AddRange([8101, 8101, 8201]);
var album = await Call<ResClaimMiniGameTextAdventurePlusEndingAlbumReward>("/MiniGame/TextAdventurePlus/Reward/Ending", endingRequest);
Assert(album.ReceivedEventTextAdvPlusEventPageTableIdList.SequenceEqual([8101]) && album.Reward.CalculateSize() > 0, "Only a discovered ending may receive its album reward");
var repeatedAlbum = await Call<ResClaimMiniGameTextAdventurePlusEndingAlbumReward>("/MiniGame/TextAdventurePlus/Reward/Ending", endingRequest);
Assert(repeatedAlbum.Reward.Currency.Count == 0 && repeatedAlbum.Reward.Item.Count == 0, "Album retries must not pay again");
Assert(!(await CheckReward()).IsRewardExist, "Claimed and ineligible rewards must not leave a reward badge");
Console.WriteLine("PASS: foreign route IDs are rejected atomically and album rewards are paid once");

foreach (int routeId in new[] { 11, 12, 13 })
    await Call<ResRemoveBadgeMiniGameTextAdventurePlusRoute>("/MiniGame/TextAdventurePlus/Route/Badge/Remove", new ReqRemoveBadgeMiniGameTextAdventurePlusRoute { EventTextAdvPlusManagerTableId = 83501, EventTextAdvPlusRouteTableId = routeId });
Assert(!(await Call<ResCheckNewRouteMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Route/Check", new ReqCheckNewRouteMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = 83501 })).IsNewRouteExist, "Seen routes must clear their badge");
var lootBadge = new ReqRemoveBadgeMiniGameTextAdventurePlusLootItem { EventTextAdvPlusManagerTableId = 83501 };
lootBadge.EventTextAdvPlusLootItemTableIdList.AddRange([loot, -1]);
Assert((await Call<ResRemoveBadgeMiniGameTextAdventurePlusLootItem>("/MiniGame/TextAdventurePlus/LootItem/Badge/Remove", lootBadge)).BadgeRemovedEventTextAdvPlusLootItemTableIdList.SequenceEqual([loot]), "Only acquired items may be marked seen");
var endingBadge = new ReqRemoveBadgeMiniGameTextAdventurePlusEnding { EventTextAdvPlusManagerTableId = 83501 };
endingBadge.EventTextAdvPlusEventPageTableIdList.AddRange([8101, 8201]);
Assert((await Call<ResRemoveBadgeMiniGameTextAdventurePlusEnding>("/MiniGame/TextAdventurePlus/Ending/Badge/Remove", endingBadge)).BadgeRemovedEndingTypeEventTextAdvPlusEventPageTableIdList.SequenceEqual([8101]), "Only discovered endings may be marked seen");
await Call<ResLogSelectOptionMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Log/Select", new ReqLogSelectOptionMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = 83501 });
await Call<ResLogInterruptMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Log/Interrupt", new ReqLogInterruptMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = 83501 });

ReloadSave();
var restored = await Enter();
Assert(restored.PlayData.PlayCount == 2 && restored.PlayData.EventTextAdvPlusEventPageTableIdList.Order().SequenceEqual(new[] { 6101, 8101 }) && restored.PlayData.AcquiredEventTextAdvPlusLootItemTableIdList.SequenceEqual([loot]) && restored.ReceivedEventTextAdvPlusProgressRewardIdList.SequenceEqual([31]) && restored.ReceivedEventTextAdvPlusEventPageTableIdList.SequenceEqual([8101]) && restored.PlayData.BadgeRemovedEventTextAdvPlusRouteTableIdList.Count == 3, "Progress, claims and seen badges must survive the real JSON save roundtrip");
Console.WriteLine("PASS: badges, telemetry and saved player data survive reload");

var secondManager = new EventTextAdvPlusManagerRecord_Raw { Id = 99999, RouteSelectGroupId = 9, RewardGroupId = 9 };
Populate("EventTextAdvPlusManagerTable", new[] { secondManager });
var separate = await Call<ResEnterMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Enter", new ReqEnterMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = 99999 });
Assert(separate.PlayData.PlayCount == 0 && separate.ReceivedEventTextAdvPlusProgressRewardIdList.Count == 0, "Minigame managers must have separate progress");
Console.WriteLine("PASS: manager progress is isolated");

await OpenRoute(11);
finish.IsRestart = true;
finish.EventTextAdvPlusEventPageTableIdList.Clear();
finish.EventTextAdvPlusEventPageTableIdList.Add(6102);
await Call<ResFinishMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Finish", finish);
Assert((await Call<ResFinishMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Finish", finish)).PlayData.PlayCount == 3, "Retrying an IsRestart finish must not settle the still-active route twice");
finish.IsRestart = false;
finish.EventTextAdvPlusEventPageTableIdList.Clear();
finish.EventTextAdvPlusEventPageTableIdList.Add(6103);
var restarted = await Call<ResFinishMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Finish", finish);
Assert(restarted.PlayData.PlayCount == 4 && restarted.PlayData.EventTextAdvPlusEventPageTableIdList.Contains(6103), "Restarting inside a route must preserve the next ending without requiring another Enter/Route");
Assert((await Call<ResFinishMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Finish", finish)).PlayData.PlayCount == 4, "Restarted finish retries must not increment play count");
Console.WriteLine("PASS: restarting within a route preserves the following result and deduplicates retries");

await OpenRoute(11);
var initialFinish = Route11Ending(6109, 113401, 1108, restart: false);
var initial = await Finish(initialFinish);
int beforeReplay = initial.PlayData.PlayCount;
// Older servers persisted only LastFinishSignature for a settled route.
var legacyRoute = JObject.Parse(File.ReadAllText(savePath))["Users"]![0]!["TextAdventurePlusDatas"]!["83501"]!;
Assert(legacyRoute["LastFinishSignature"] != null && legacyRoute["EarlierFinishSignatures"] == null,
    "An ordinary settlement must keep the legacy single-receipt save format");
ReloadSave();
var otherRoute = new ReqFinishMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = 83501, EventTextAdvPlusRouteTableId = 12, IsRestart = true };
otherRoute.EventTextAdvPlusEventPageTableIdList.Add(7201);
Assert((await Finish(otherRoute)).PlayData.Equals(initial.PlayData), "An inline restart must not switch to a different route after settlement");
var inline = await Finish(inlineFinish);
Assert(inline.PlayData.PlayCount == beforeReplay + 1 && inline.PlayData.EventTextAdvPlusEventPageTableIdList.Contains(6110) &&
    inline.PlayData.EventTextAdvPlusSelectOptionTableIdList.Contains(113701) && inline.PlayData.AcquiredEventTextAdvPlusLootItemTableIdList.Contains(1109),
    "Replaying after a normal finish without Enter/Route must save the new ending, choice and loot");
Assert((await Finish(inlineFinish)).PlayData.Equals(inline.PlayData), "Retrying the inline settlement must return the same progress");
var next = await Finish(Route11Ending(6111, 113901, 1118, restart: true));
Assert(next.PlayData.PlayCount == beforeReplay + 2, "A second distinct inline ending must count once");
ReloadSave();
Assert((await Finish(inlineFinish)).PlayData.Equals(next.PlayData) && (await Finish(initialFinish)).PlayData.Equals(next.PlayData),
    "Delayed retries from the same route visit must not add plays or change unlocks after reload");
var afterReplay = (await Enter()).PlayData;
Assert(afterReplay.EventTextAdvPlusEventPageTableIdList.Contains(6110) && afterReplay.EventTextAdvPlusEventPageTableIdList.Contains(6111) &&
    afterReplay.AcquiredEventTextAdvPlusLootItemTableIdList.Contains(1109) && afterReplay.AcquiredEventTextAdvPlusLootItemTableIdList.Contains(1118),
    "Both replay endings and memory items must survive re-entry from disk");
await OpenRoute(12);
await Reject(() => Finish(inlineFinish));
await OpenRoute(11);
Assert((await Finish(initialFinish)).PlayData.PlayCount == beforeReplay + 3, "Explicitly opening a new run must allow an identical settlement again");
Console.WriteLine("PASS: normal finish followed by inline replays saves both collections across reload and counts each distinct settlement once");

user = new User { ID = 2, Currency = [] };
typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb", true)!.GetProperty("Instance")!.SetValue(null, new CoreInfo { Users = [user], BondRewardLevelsMigrated = true });
db.Users.Add(new GameUser { ID = user.ID });
db.SaveChanges();
await OpenRoute(11);
finish.EventTextAdvPlusEventPageTableIdList.Clear();
finish.EventTextAdvPlusEventPageTableIdList.Add(8101);
await Call<ResFinishMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Finish", finish);
var beforeFailure = JsonConvert.SerializeObject(user);
var beforeTriggers = db.Triggers.Count();
var blockingPath = Path.Combine(AppContext.BaseDirectory, "db.json.tmp");
Directory.CreateDirectory(blockingPath);
bool saveFailed = false;
try
{
    await Call<ResClaimMiniGameTextAdventurePlusEndingAlbumReward>("/MiniGame/TextAdventurePlus/Reward/Ending", endingRequest);
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException) { saveFailed = true; }
finally { Directory.Delete(blockingPath); }
Assert(saveFailed, "The injected JSON write failure must be observed");
Assert(JsonConvert.SerializeObject(user) == beforeFailure, "Failed reward save must restore the exact player state, including an empty currency dictionary");
Assert(db.Triggers.Count() == beforeTriggers, "Failed reward save must not leave SQLite music triggers behind");
var recovered = await Call<ResClaimMiniGameTextAdventurePlusEndingAlbumReward>("/MiniGame/TextAdventurePlus/Reward/Ending", endingRequest);
Assert(recovered.ReceivedEventTextAdvPlusEventPageTableIdList.SequenceEqual([8101]) && db.Triggers.Count() == beforeTriggers + 1, "Retry after a failed save must deliver one album reward and one trigger");
Console.WriteLine("PASS: failed save rolls back music, claims and exact currency; retry delivers once");

void Populate<T>(string fieldName, T[] rows)
{
    if (typeof(GameData).GetField(fieldName)?.GetValue(data) is not System.Collections.IDictionary table) return;
    foreach (var row in rows) table.Add((int)typeof(T).GetField("Id")!.GetValue(row)!, row);
}
void ReloadSave()
{
    var saved = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(savePath))!;
    user = saved.Users.Single();
    typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb", true)!.GetProperty("Instance")!.SetValue(null, saved);
}
ReqFinishMiniGameTextAdventurePlus Route11Ending(int ending, int option, int lootId, bool restart)
{
    var request = new ReqFinishMiniGameTextAdventurePlus
    {
        EventTextAdvPlusManagerTableId = 83501, EventTextAdvPlusRouteTableId = 11,
        EventTextAdvPlusFirstSelectTableId = 1101, IsRestart = restart
    };
    request.EventTextAdvPlusEventPageTableIdList.Add(ending);
    request.EventTextAdvPlusSelectOptionTableIdList.Add(option);
    request.AcquiredEventTextAdvPlusLootItemTableIdList.Add(lootId);
    return request;
}
Task<ResFinishMiniGameTextAdventurePlus> Finish(ReqFinishMiniGameTextAdventurePlus request) =>
    Call<ResFinishMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Finish", request);
Task<ResEnterMiniGameTextAdventurePlus> Enter() => Call<ResEnterMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Enter", new ReqEnterMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = 83501 });
Task<ResEnterMiniGameTextAdventurePlusRoute> OpenRoute(int routeId) => Call<ResEnterMiniGameTextAdventurePlusRoute>("/MiniGame/TextAdventurePlus/Enter/Route", new ReqEnterMiniGameTextAdventurePlusRoute { EventTextAdvPlusManagerTableId = 83501, EventTextAdvPlusRouteTableId = routeId });
Task<ResCheckRewardMiniGameTextAdventurePlus> CheckReward() => Call<ResCheckRewardMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Reward/Check", new ReqCheckRewardMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = 83501 });
Task<ResClaimMiniGameTextAdventurePlusProgressReward> ClaimProgress(params int[] ids)
{
    var request = new ReqClaimMiniGameTextAdventurePlusProgressReward { EventTextAdvPlusManagerTableId = 83501 };
    request.EventTextAdvPlusProgressRewardIdList.AddRange(ids);
    return Call<ResClaimMiniGameTextAdventurePlusProgressReward>("/MiniGame/TextAdventurePlus/Reward/Progress", request);
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Reject(Func<Task> action)
{
    try { await action(); } catch (BadHttpRequestException e) when (e.StatusCode == 400) { return; }
    throw new Exception("Invalid minigame request was accepted");
}

async Task<T> Call<T>(string path, IMessage request, bool authenticated = true) where T : IMessage<T>, new()
{
    var ctx = new DefaultHttpContext { RequestServices = services };
    if (authenticated) ctx.Items["UserID"] = user.ID;
    using var input = new MemoryStream(request.ToByteArray());
    using var output = new MemoryStream();
    ctx.Request.Body = input;
    ctx.Response.Body = output;
    var handler = LobbyHandler.Handlers.TryGetValue(path, out var registered)
        ? (LobbyMessage)Activator.CreateInstance(registered.GetType())! : new EmptyHandler();
    await handler.HandleAsync(ctx);
    if (ctx.Response.StatusCode >= 400) throw new BadHttpRequestException("Request rejected", ctx.Response.StatusCode);
    var response = new T();
    response.MergeFrom(output.ToArray());
    return response;
}
