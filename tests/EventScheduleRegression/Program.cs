using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.LobbyServer.Event;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

var liveRoot = args.SingleOrDefault(a => a.StartsWith("--probe-live-root="));
if (liveRoot != null) return await LiveChecks.Run(liveRoot["--probe-live-root=".Length..]);

// All loaded data and database output belong to this session's synthetic workspace.
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
var progress = new Progress<double>();
foreach (var row in await data.LoadZip<EventManagerRecord>("EventManagerTable.json", progress)) data.eventManagers.Add(row.Id, row);
foreach (var row in await data.LoadZip<LobbyPrivateBannerRecord>("LobbyPrivateBannerTable.json", progress)) data.LobbyPrivateBannerTable.Add(row.Id, row);
foreach (var row in await data.LoadZip<DailyMissionEventSettingRecord_Raw>("DailyMissionEventSettingTable.json", progress)) data.DailyMissionEventSettingTable.Add(row.Id, row);

const int rootEvent = 83500, bannerId = 10124;
int[] related = [83500, 40124, 40125, 60124, 100053, 83501, 83502, 83503];
long expectedEnd = new DateTime(2026, 11, 18, 19, 59, 59, DateTimeKind.Utc).Ticks;
var user = new User { ID = 83701, LobbyPrivateBannerIds = [bannerId] };
user.EventMissionInfo[83503] = new EventMissionData
{
    MissionIdList = [83503001], DailyMissionIdList = [83503002], LastDay = 20261114,
    LastDate = new DateTime(2026, 11, 14, 15, 0, 0, DateTimeKind.Utc).Ticks, AllClear = true
};
var state = new CoreInfo { Users = [user], ActiveEventBannerIds = [bannerId] };
typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb", true)!.GetProperty("Instance")!.SetValue(null, state);
var dbPath = Path.Combine(AppContext.BaseDirectory, "event-schedule.db");
GameContext.InitializeOptions($"Data Source={dbPath}", "sqlite");
using var db = GameContext.CreateNew();
db.Database.EnsureDeleted();
db.Database.EnsureCreated();
db.SdkUsers.Add(new SdkUser { ID = user.ID, Email = "synthetic", PlayerName = "synthetic", PasswordHash = "unused", RegisterTime = 1788134400 });
db.SaveChanges();
using var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
int passed = 0, failed = 0;
var banner = data.LobbyPrivateBannerTable[bannerId];
Check(banner.EventId == rootEvent && banner.PrivateBannerResource == "btn_event_enter_marian", "MARIA-n fixture changed");

await Test("event list sends the real UTC deadline for MARIA-n and all eight activity records", async () =>
{
    var packet = await List();
    foreach (int id in related) AssertDeadline(packet.EventList.Single(e => e.Id == id));
});
await Test("joined event response uses the same fixed end and disable dates", async () =>
{
    var packet = await Joined();
    foreach (int id in related) AssertDeadline(packet.EventWithJoinData.Single(e => e.EventData.Id == id).EventData);
});
await Test("refreshing both responses never extends an activity's deadline", async () =>
{
    var first = await List();
    await Task.Delay(20);
    var second = await List();
    var joined = await Joined();
    foreach (int id in related)
    {
        long end = first.EventList.Single(e => e.Id == id).EventEndDate;
        Check(end == second.EventList.Single(e => e.Id == id).EventEndDate &&
              end == joined.EventWithJoinData.Single(e => e.EventData.Id == id).EventData.EventEndDate,
            $"event {id} deadline moved after a refresh");
    }
});
await Test("the screenshot time has four days remaining instead of twenty-nine", async () =>
{
    long end = (await List()).EventList.Single(e => e.Id == 83503).EventEndDate;
    var screenshotUtc = new DateTime(2026, 11, 14, 15, 6, 39, DateTimeKind.Utc);
    Check(new DateTime(end, DateTimeKind.Utc) - screenshotUtc == new TimeSpan(4, 4, 53, 20), "incorrect UTC+9 to UTC deadline conversion");
});
await Test("date reads preserve all saved claims, start dates and the 04:00 Beijing daily reset", async () =>
{
    string before = JsonConvert.SerializeObject(user);
    var list = await List();
    var joined = await Joined();
    Check(JsonConvert.SerializeObject(user) == before, "date read changed player rewards, claims or progress");
    Check(state.ResetHourUtcTime == 20, "daily reset moved away from 20:00 UTC");
    foreach (int id in related)
        Check(list.EventList.Single(e => e.Id == id).EventStartDate > 0 &&
              joined.EventWithJoinData.Single(e => e.EventData.Id == id).EventData.EventStartDate > 0, "event start was lost");
    int dailyId = data.DailyMissionEventSettingTable.Values.Select(x => x.EventId).First(id => !related.Contains(id));
    long start = DateTimeOffset.FromUnixTimeSeconds(1788134400).UtcDateTime.AddDays(-21).Ticks;
    Check(list.EventList.Single(e => e.Id == dailyId).EventStartDate == start &&
          joined.EventWithJoinData.Single(e => e.EventData.Id == dailyId).EventData.EventStartDate == start,
        "stable daily-event start changed and could reopen claimed missions");
});
await Test("unrelated private events retain their original duration", async () =>
{
    const int privateBanner = 999000, privateEvent = 999001;
    data.LobbyPrivateBannerTable[privateBanner] = new() { Id = privateBanner, EventId = privateEvent };
    data.eventManagers[privateEvent] = new() { Id = privateEvent, EventSystemType = EventSystemType.ShopEvent, EventBannerResourceTable = "" };
    user.LobbyPrivateBannerIds.Add(privateBanner);
    var before = DateTime.UtcNow.AddDays(30).Ticks;
    var list = await List();
    var joined = await Joined();
    var after = DateTime.UtcNow.AddDays(30).Ticks;
    foreach (var entry in new[] { list.EventList.Single(e => e.Id == privateEvent), joined.EventWithJoinData.Single(e => e.EventData.Id == privateEvent).EventData })
        Check(entry.EventEndDate >= before && entry.EventEndDate <= after && entry.EventDisableDate >= before && entry.EventDisableDate <= after,
            "MARIA-n deadline leaked into an unrelated private event");
    user.LobbyPrivateBannerIds.Remove(privateBanner);
});
await Test("both parent link fields give newly related activities the same deadline", async () =>
{
    foreach (bool viaField in new[] { false, true })
    {
        int id = viaField ? 999011 : 999010;
        data.eventManagers[id] = new() { Id = id, EventSystemType = EventSystemType.ShopEvent, EventBannerResourceTable = "",
            ParentsEventId = viaField ? 0 : rootEvent, SetField = viaField ? rootEvent : 0 };
        AssertDeadline((await List()).EventList.Single(e => e.Id == id));
        AssertDeadline((await Joined()).EventWithJoinData.Single(e => e.EventData.Id == id).EventData);
    }
});

Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

void AssertDeadline(NetEventData entry) => Check(entry.EventEndDate == expectedEnd && entry.EventDisableDate == expectedEnd,
    $"event {entry.Id}: expected 2026-11-18T19:59:59Z, got end {new DateTime(entry.EventEndDate):O} / disable {new DateTime(entry.EventDisableDate):O}");
static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
async Task Test(string name, Func<Task> run)
{
    try { await run(); passed++; Console.WriteLine("PASS: " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL: " + name + ": " + ex.Message); }
}
async Task<ResGetEventList> List() => ResGetEventList.Parser.ParseFrom(await Send(new ListEvents(), new ReqGetEventList()));
async Task<ResGetJoinedEvent> Joined() => ResGetJoinedEvent.Parser.ParseFrom(await Send(new GetJoinedEvent(), new ReqGetJoinedEvent()));
async Task<byte[]> Send(LobbyMessage handler, IMessage request)
{
    var context = new DefaultHttpContext { RequestServices = services };
    context.Items["UserID"] = user.ID;
    using var input = new MemoryStream(request.ToByteArray());
    using var output = new MemoryStream();
    context.Request.Body = input;
    context.Response.Body = output;
    await handler.HandleAsync(context);
    return output.ToArray();
}
