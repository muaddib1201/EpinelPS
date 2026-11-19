using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.LobbyServer.Event;
using EpinelPS.LobbyServer.Gacha;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
foreach (var row in await data.LoadZip<GachaTypeRecord>("GachaTypeTable.json", progress)) data.gachaTypes.Add(row.Id, row);
foreach (var row in await data.LoadZip<GachaPaybackRecord_Raw>("GachaPaybackTable.json", progress)) data.GachaPaybackRecords.Add(row.Id, row);

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
var clock = new AdminScheduleChecks.ScheduleClock(new DateTime(expectedEnd - 1, DateTimeKind.Utc));
using var services = new ServiceCollection().AddSingleton(db).AddSingleton<TimeProvider>(clock).BuildServiceProvider();
int passed = 0, failed = 0;
var banner = data.LobbyPrivateBannerTable[bannerId];
Check(banner.EventId == rootEvent && banner.PrivateBannerResource == "btn_event_enter_marian", "MARIA-n fixture changed");

await Test("empty lobby skips the missing Sky sprite table while retaining both Snow pickups", async () =>
{
    var personal = user.LobbyPrivateBannerIds;
    var global = state.ActiveEventBannerIds;
    var periods = state.MiracleSnowRecruitment;
    var now = clock.GetUtcNow();
    try
    {
        user.LobbyPrivateBannerIds = [];
        state.ActiveEventBannerIds = [];
        state.MiracleSnowRecruitment = new() { [101] = new(now, now.AddDays(21)), [102] = new(now, now.AddDays(21)) };
        Check(data.LobbyPrivateBannerTable.Values.Last().EventId == 40126, "expected current Sky fallback fixture");
        var list = await List();
        var joined = await Joined();
        foreach (var records in new[] { list.EventList.AsEnumerable(), joined.EventWithJoinData.Select(x => x.EventData) })
        {
            Check(!records.Any(x => x.Id is 40126 or 60126), "missing lobby art was still advertised");
            foreach (int id in new[] { 70005, 70006 })
                Check(records.Single(x => x.Id == id).EventEndDate == now.AddDays(21).UtcTicks, "pickup schedule changed");
        }
        state.ActiveEventBannerIds = [10126];
        Check(EventHelper.GetLobbyPrivateBannerData(user, now.UtcDateTime).Single().EventId == 40126, "explicit selection was suppressed");
    }
    finally { user.LobbyPrivateBannerIds = personal; state.ActiveEventBannerIds = global; state.MiracleSnowRecruitment = periods; }
});

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
    var expected = clock.GetUtcNow().AddDays(30).Ticks;
    var list = await List();
    var joined = await Joined();
    foreach (var entry in new[] { list.EventList.Single(e => e.Id == privateEvent), joined.EventWithJoinData.Single(e => e.EventData.Id == privateEvent).EventData })
        Check(entry.EventEndDate == expected && entry.EventDisableDate == expected,
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

await Test("a saved main-event deadline overrides MARIA-n for list, joined and login details", async () =>
{
    await WithDeadlines(new() { [rootEvent] = new DateTime(2030, 1, 2, 19, 59, 59, DateTimeKind.Utc) }, async () =>
    {
        var list = await List();
        var joined = await Joined();
        long expected = new DateTime(2030, 1, 2, 19, 59, 59, DateTimeKind.Utc).Ticks;
        foreach (int id in related)
            foreach (var entry in new[] { list.EventList.Single(e => e.Id == id), joined.EventWithJoinData.Single(e => e.EventData.Id == id).EventData })
                Check(entry.EventEndDate == expected && entry.EventDisableDate == expected, $"saved root deadline was ignored for event {id}");
        var login = ResLoginEventData.Parser.ParseFrom(await Send(new EventLoginGet(), new ReqLoginEventData { EventId = 100053 }));
        Check(login.EndDate == expected && login.DisableDate == expected, "login details have a separate drifting deadline");
    });
});
await Test("nested parent and set-field children inherit the root deadline without affecting another group", async () =>
{
    data.eventManagers[999020] = new() { Id = 999020, ParentsEventId = 40124 };
    data.eventManagers[999021] = new() { Id = 999021, SetField = 999020 };
    data.eventManagers[999022] = new() { Id = 999022, ParentsEventId = 999021, SetField = 999020 };
    await WithDeadlines(new() { [rootEvent] = new DateTime(2030, 1, 2, 19, 59, 59, DateTimeKind.Utc),
        [999001] = new DateTime(2030, 2, 3, 4, 5, 6, DateTimeKind.Utc) }, async () =>
    {
        var list = await List();
        var joined = await Joined();
        foreach (int id in new[] { 999020, 999021, 999022 })
        {
            Check(list.EventList.Any(e => e.Id == id) && joined.EventWithJoinData.Any(e => e.EventData.Id == id), $"nested event {id} is missing from public event responses");
            Check(ResolveEnd(id, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)) == new DateTime(2030, 1, 2, 19, 59, 59, DateTimeKind.Utc),
                $"nested event {id} did not use the main deadline");
        }
        Check(ResolveEnd(999001, DateTime.UtcNow) == new DateTime(2030, 2, 3, 4, 5, 6, DateTimeKind.Utc), "an unrelated root lost its own deadline");
    });
});
await Test("mixed recruitment, login, payback and free-recruit links include every descendant in public responses", async () =>
{
    data.eventManagers[999050] = new() { Id = 999050, ParentsEventId = rootEvent, EventSystemType = EventSystemType.ShopEvent, EventBannerResourceTable = "event_schedule_fixture" };
    data.eventManagers[999051] = new() { Id = 999051, EventSystemType = EventSystemType.LoginEvent, EventBannerResourceTable = "event_schedule_fixture" };
    data.eventManagers[999052] = new() { Id = 999052, ParentsEventId = 999051, EventSystemType = EventSystemType.ShopEvent, EventBannerResourceTable = "" };
    data.eventManagers[999053] = new() { Id = 999053, SetField = 999052, EventSystemType = EventSystemType.ShopEvent, EventBannerResourceTable = "" };
    data.eventManagers[999054] = new() { Id = 999054, EventSystemType = EventSystemType.PickupGachaEvent, EventBannerResourceTable = "event_schedule_fixture" };
    data.eventManagers[999055] = new() { Id = 999055, EventSystemType = EventSystemType.ShopEvent, EventBannerResourceTable = "" };
    data.eventManagers[999056] = new() { Id = 999056, ParentsEventId = 999055, EventSystemType = EventSystemType.ShopEvent, EventBannerResourceTable = "" };
    data.eventManagers[999057] = new() { Id = 999057, EventSystemType = EventSystemType.ShopEvent, EventBannerResourceTable = "" };
    data.eventManagers[999058] = new() { Id = 999058, SetField = 999057, EventSystemType = EventSystemType.ShopEvent, EventBannerResourceTable = "" };
    data.gachaTypes[999060] = new() { Id = 999060, EventId = 999054, DailyFreeGachaEventId = 999057 };
    data.GachaPaybackRecords[999061] = new() { Id = 999061, GachaId = 999060, EventId = 999055 };
    data.eventManagers[999071] = new() { Id = 999071, ParentsEventId = 999001, EventSystemType = EventSystemType.ShopEvent, EventBannerResourceTable = "" };
    data.eventManagers[999080] = new() { Id = 999080, ParentsEventId = 999081 };
    data.eventManagers[999081] = new() { Id = 999081, SetField = 999080 };
    await WithDeadlines(new() { [rootEvent] = new DateTime(2030, 1, 2, 19, 59, 59, DateTimeKind.Utc) }, async () =>
    {
        var list = await List();
        var joined = await Joined();
        foreach (int id in Enumerable.Range(999050, 9))
        {
            var entry = list.EventList.SingleOrDefault(e => e.Id == id);
            var other = joined.EventWithJoinData.SingleOrDefault(e => e.EventData.Id == id)?.EventData;
            Check(entry != null && other != null, $"indirect child {id} is missing from public responses");
            Check(entry!.EventEndDate == new DateTime(2030, 1, 2, 19, 59, 59, DateTimeKind.Utc).Ticks && other!.EventDisableDate == entry.EventEndDate, $"indirect child {id} lost the root deadline");
        }
        Check(list.EventList.All(e => e.Id != 999071), "a child from an unrelated group leaked into this activity");
        Check(ResolveEnd(999080, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)) == new DateTime(2030, 1, 31, 0, 0, 0, DateTimeKind.Utc), "an unowned relationship cycle did not retain its legacy fallback");
    });
});
await Test("daily free-recruit detail uses the owning activity deadline", async () =>
{
    await WithDeadlines(new() { [rootEvent] = new DateTime(2030, 1, 2, 19, 59, 59, DateTimeKind.Utc) }, async () =>
    {
        var response = ResCheckDailyFreeGacha.Parser.ParseFrom(await Send(new CheckGachaDailyEvent(), new ReqCheckDailyFreeGacha { GachaId = 999060 }));
        long expected = new DateTime(2030, 1, 2, 19, 59, 59, DateTimeKind.Utc).Ticks;
        Check(response.EventData?.EventEndDate == expected && response.EventData.EventDisableDate == expected, "free-recruit detail used its independent rolling deadline");
    });
});
await Test("unconfigured login and daily free-recruit details retain their legacy local wire ticks", async () =>
{
    data.eventManagers[999090] = new() { Id = 999090, EventSystemType = EventSystemType.LoginEvent, EventBannerResourceTable = "" };
    data.gachaTypes[999091] = new() { Id = 999091, DailyFreeGachaEventId = 999090 };
    long before = DateTime.Now.AddDays(13).Ticks;
    var login = ResLoginEventData.Parser.ParseFrom(await Send(new EventLoginGet(), new ReqLoginEventData { EventId = 999090 }));
    Check(login.EndDate >= before && login.EndDate <= DateTime.Now.AddDays(13).Ticks, "unedited login deadline shifted from local to UTC ticks");
    before = DateTime.Now.AddDays(20).Ticks;
    var free = ResCheckDailyFreeGacha.Parser.ParseFrom(await Send(new CheckGachaDailyEvent(), new ReqCheckDailyFreeGacha { GachaId = 999091 }));
    Check(free.EventData.EventEndDate >= before && free.EventData.EventEndDate <= DateTime.Now.AddDays(20).Ticks, "unedited free-recruit deadline shifted from local to UTC ticks");
});
await Test("a configured countdown stays fixed across its deadline and a configuration round trip", async () =>
{
    await WithDeadlines(new() { [rootEvent] = new DateTime(2030, 1, 2, 19, 59, 59, DateTimeKind.Utc) }, () =>
    {
        foreach (var now in new[] { new DateTime(2030, 1, 2, 19, 59, 58, DateTimeKind.Utc),
            new DateTime(2030, 1, 2, 20, 0, 0, DateTimeKind.Utc), new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc) })
            Check(ResolveEnd(rootEvent, now) == new DateTime(2030, 1, 2, 19, 59, 59, DateTimeKind.Utc), "refresh or expiry silently renewed the saved countdown");
        return Task.CompletedTask;
    });
});

await Test("expired personal and global banners stay out of game lists, including the latest fallback", async () =>
{
    clock.Set(new DateTime(expectedEnd, DateTimeKind.Utc));
    foreach (bool personal in new[] { true, false })
    {
        if (!personal) user.LobbyPrivateBannerIds.Clear();
        var list = await List();
        var joined = await Joined();
        Check(!list.EventList.Any(e => related.Contains(e.Id)) && !joined.EventWithJoinData.Any(e => related.Contains(e.EventData.Id)),
            "expired main event or child appeared at the deadline");
    }
    var latest = data.LobbyPrivateBannerTable.Values.Last();
    await WithDeadlines(new() { [latest.EventId] = clock.GetUtcNow().UtcDateTime }, async () =>
    {
        var list = await List();
        Check(!list.EventList.Any(e => e.Id == latest.EventId), "latest fallback resurrected an expired banner");
        Check(list.EventList.Count > 0, "expiry removed independent daily events");
    });
    Check(state.ActiveEventBannerIds.Count == 0, "expired global selection was not cleared");
});

await Test("Miracle Snow sends only two pickup events with fixed, independently configurable periods", async () =>
{
    var originalClock = clock.GetUtcNow();
    var start = originalClock.AddDays(2);
    var initialize = typeof(User).Assembly.GetType("EpinelPS.Services.MiracleSnowRecruitment")!
        .GetMethod("Initialize", BindingFlags.NonPublic | BindingFlags.Static)!;
    initialize.Invoke(null, [state, start]);
    var reloaded = JsonConvert.DeserializeObject<CoreInfo>(JsonConvert.SerializeObject(state))!;
    initialize.Invoke(null, [reloaded, start.AddDays(30)]);
    Check(reloaded.MiracleSnowRecruitment!.Count == 2 && new[] {101, 102}.All(id =>
        reloaded.MiracleSnowRecruitment[id].Start == start && reloaded.MiracleSnowRecruitment[id].End == start.AddDays(21)),
        "restart changed the 21-day schedule");
    try
    {
        foreach (var instant in new[] { start.AddTicks(-1), start, start.AddDays(21).AddTicks(-1), start.AddDays(21) })
        {
            clock.Set(instant.UtcDateTime);
            var list = await List();
            var joined = await Joined();
            var gacha = ResGetGachaData.Parser.ParseFrom(await Send(new GetGacha(), new ReqGetGachaData()));
            bool active = instant >= start && instant < start.AddDays(21);
            foreach (var (poolId, eventId) in new[] { (101, 70005), (102, 70006) })
            {
                Check(gacha.Gacha.Count(g => g.GachaType == poolId) == (active ? 1 : 0), "incorrect recruit page availability");
                foreach (var records in new[] { list.EventList.AsEnumerable(), joined.EventWithJoinData.Select(e => e.EventData) })
                {
                    var entry = records.SingleOrDefault(e => e.Id == eventId);
                    Check((entry != null) == active, "wrong activation boundary");
                    if (entry != null) Check(entry.EventSystemType == (int)EventSystemType.PickupGachaEvent &&
                        entry.EventVisibleDate == start.UtcTicks && entry.EventStartDate == start.UtcTicks &&
                        entry.EventEndDate == start.AddDays(21).UtcTicks && entry.EventDisableDate == entry.EventEndDate, "wrong pickup dates");
                }
            }
            Check(!list.EventList.Any(e => e.Id == 80100 || e.Id == 70007), "a story banner or unrelated legacy pool was enabled");
        }
        state.MiracleSnowRecruitment!.Remove(101);
        clock.Set(start.UtcDateTime);
        var separate = await List();
        Check(!separate.EventList.Any(e => e.Id == 70005) && separate.EventList.Any(e => e.Id == 70006), "pools cannot be configured separately");
    }
    finally { state.MiracleSnowRecruitment = null; clock.Set(originalClock.UtcDateTime); }
});

var adminChecks = await AdminScheduleChecks.Run(data, user.ID, db, workspace);
passed += adminChecks.Passed;
failed += adminChecks.Failed;
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
DateTime ResolveEnd(int id, DateTime now) => (DateTime)typeof(EventHelper).GetMethod("GetEventEnd", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [id, now])!;
async Task WithDeadlines(Dictionary<int, DateTime> deadlines, Func<Task> run)
{
    var property = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb", true)!.GetProperty("Instance")!;
    var original = property.GetValue(null);
    var json = JObject.FromObject(original!);
    json["EventEndUtc"] = JObject.FromObject(deadlines);
    property.SetValue(null, json.ToObject<CoreInfo>()!);
    try { await run(); }
    finally { property.SetValue(null, original); }
}
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
