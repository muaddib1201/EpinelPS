using System.Reflection;
using EpinelPS;
using Google.Protobuf.WellKnownTypes;

// The arena badge probe only changes replies while arena-badge-probe.json exists, re-reads
// it without a restart, and ignores invalid files so a typo cannot affect players.
try
{
    var probe = typeof(ResGetArena).Assembly.GetType("EpinelPS.LobbyServer.Arena.ArenaBadgeProbe")!;
    object? Call(string name, params object[] args) => probe.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    var file = Path.Combine(AppContext.BaseDirectory, "probe-test.json");
    if (File.Exists(file)) File.Delete(file);
    probe.GetProperty("FilePath", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, file);
    var now = new DateTime(2026, 11, 18, 2, 0, 0, DateTimeKind.Utc);
    var entered = Timestamp.FromDateTime(now.AddMinutes(-5));
    ResPermanentContentBadgeData Badge()
    {
        var response = new ResPermanentContentBadgeData();
        response.ContentEnterTimes.Add(new NetContentEnterTimeData { ContentType = NetEnteredContentType.Tower, EnteredAt = entered });
        response.ContentEnterTimes.Add(new NetContentEnterTimeData { ContentType = NetEnteredContentType.Arena, EnteredAt = entered });
        Call("ApplyEnterTime", response, now);
        return response;
    }
    NetSchedule[] Schedules()
    {
        var schedules = new ResGetArena { Schedules = { new NetSchedule { OnlyOnce = new() { StartAt = 1, EndAt = 2 } } } }.Schedules;
        Call("ApplySchedule", schedules);
        return schedules.ToArray();
    }
    void Write(string json) { File.WriteAllText(file, json); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000))); }

    var unchanged = Badge();
    Check(unchanged.ContentEnterTimes.Count == 2 && unchanged.ContentEnterTimes[1].EnteredAt == entered &&
        (bool)Call("ApplyRankingNewReward", true)! && (int)Call("ApplyFreeCount", true, 1, 2)! == 1 &&
        (long)Call("ApplySpecialReward", 37L)! == 37 && Schedules().Single().OnlyOnce.EndAt == 2,
        "without the probe file every reply stays unchanged");
    Console.WriteLine("PASS: no probe file leaves all arena badge replies unchanged");

    Write("""{ "EnterTime": "omit", "RankingNewReward": "false", "SpecialFree": "used", "RookieFree": "used", "SpecialReward": "zero", "Schedule": "alltime" }""");
    var omitted = Badge();
    Check(omitted.ContentEnterTimes.Single().ContentType == NetEnteredContentType.Tower, "omit removes only the arena entry");
    Check(!(bool)Call("ApplyRankingNewReward", true)!, "ranking flag can be forced off");
    Check((int)Call("ApplyFreeCount", true, 0, 2)! == 2 && (int)Call("ApplyFreeCount", false, 1, 5)! == 5 && (int)Call("ApplyFreeCount", true, 3, 2)! == 3,
        "free attempts are reported as spent without lowering a higher real count");
    Check((long)Call("ApplySpecialReward", 37L)! == 0, "reward box can be reported empty");
    Check(Schedules().Single().ScheduleKindCase == NetSchedule.ScheduleKindOneofCase.AllTime, "schedule can be replaced");
    Write("""{ "EnterTime": "now", "Schedule": "omit" }""");
    Check(Badge().ContentEnterTimes.Single(t => t.ContentType == NetEnteredContentType.Arena).EnteredAt == Timestamp.FromDateTime(now) &&
        Schedules().Length == 0 && (bool)Call("ApplyRankingNewReward", true)!, "edited file is re-read without restart; unspecified switches stay default");
    Write("""{ "EnterTime": "epoch" }""");
    Check(Badge().ContentEnterTimes.Single(t => t.ContentType == NetEnteredContentType.Arena).EnteredAt.Seconds == 0, "epoch entry");
    NetChampionArenaBadgeData Closed() => new() { ChampionArenaContentsState = ChampionArenaContentsState.Closed };
    var user = new NetUserData { ArenaCount = 1, SpecialArenaCount = 0 };
    Call("ApplyUserData", user);
    Check(Call("ApplyChampion", Closed()) is NetChampionArenaBadgeData { ChampionArenaContentsState: ChampionArenaContentsState.Closed } &&
        user.ArenaCount == 1 && user.SpecialArenaCount == 0, "champion data and login counts stay unchanged unless selected");
    Write("""{ "Champion": "omit", "RookieFree": "used" }""");
    var omittedEntry = new ResGetChampionArena { ChampionArenaContentsState = ChampionArenaContentsState.Closed, BadgeData = Closed() };
    Call("ApplyChampionEntry", omittedEntry);
    Call("ApplyUserData", user);
    Check(Call("ApplyChampion", Closed()) == null && omittedEntry.BadgeData == null && user.ArenaCount == 5 && user.SpecialArenaCount == 0,
        "champion badge data can be left out and login rookie attempts reported as spent");
    Write("""{ "Champion": "seasonclosed" }""");
    var entry = new ResGetChampionArena { ChampionArenaContentsState = ChampionArenaContentsState.Closed, BadgeData = Closed() };
    Call("ApplyChampionEntry", entry);
    Check(entry.ChampionArenaContentsState == ChampionArenaContentsState.SeasonClosed &&
        entry.BadgeData.ChampionArenaContentsState == ChampionArenaContentsState.SeasonClosed, "champion state variant applies to entry and badge data");
    Console.WriteLine("PASS: each switch changes only its reply field and edits apply without restart");

    Write("""{ "EnterTime": "sometimes" }""");
    Check(Badge().ContentEnterTimes[1].EnteredAt == entered, "an invalid value is ignored");
    Write("{ not json");
    Check(Badge().ContentEnterTimes[1].EnteredAt == entered && (long)Call("ApplySpecialReward", 37L)! == 37, "unreadable file is ignored");
    File.Delete(file);
    Check(Badge().ContentEnterTimes[1].EnteredAt == entered, "deleting the file restores normal replies");
    Console.WriteLine("PASS: invalid or removed probe files fall back to normal replies");
    return 0;
}
catch (Exception ex) { Console.WriteLine(ex); return 1; }

static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
