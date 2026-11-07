using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Services;
using EpinelPS.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

try
{
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
    if (root == null) throw new Exception("Missing isolated data root");
    if (args.FirstOrDefault(a => a.StartsWith("--probe-live-root=")) is string liveRoot)
        return await FriendLiveChecks.Run(liveRoot["--probe-live-root=".Length..]);
    var assembly = typeof(GameContext).Assembly;
    var service = assembly.GetType("EpinelPS.LobbyServer.Friend.FriendshipService");
    Check(service != null, "persistent friendship service is available");
    string fixture = Path.Combine(root.FullName, "Server and Server selector");
    var pack = Directory.GetFiles(Path.Combine(fixture, "cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
    var data = new GameData(pack);
    typeof(GameData).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, data);
    await GameDataInitializer.InitializeGameData(new Progress<double>());
    if (args.FirstOrDefault(a => a.StartsWith("--audit-file=")) is string auditFile)
        return FriendAudit.Run(auditFile["--audit-file=".Length..], args.Single(a => a.StartsWith("--audit-output="))["--audit-output=".Length..]);
    var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    string path = Path.Combine(AppContext.BaseDirectory, "db.json");
    var jsonType = assembly.GetType("EpinelPS.Database.JsonDb")!;
    var instance = jsonType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
    GameContext.InitializeOptions($"Data Source={Path.Combine(AppContext.BaseDirectory, "friends.db")}", "sqlite");
    if (args.Contains("--verify-replay-saved"))
    {
        var restarted = (CoreInfo)instance.GetValue(null)!;
        var expected = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "receipt-expected.json")));
        var user = restarted.Users.Single(u => u.ID == 1552239268);
        var replay = (ResObtainFriendshipPoint)Call("Obtain", user, (ulong)expected["friend"]!, now.AddDays(2), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Check(replay.Result == FriendshipPointResult.Expired && user.GetCurrencyVal(CurrencyType.FriendshipPoint) == (long)expected["balance"]!, "evicted consumed request stays rejected after restart and another reset");
        Console.WriteLine("PASS: consumed packet identity survives response-cache eviction, restart and subsequent daily reset");
        return 0;
    }
    if (args.Contains("--verify-saved"))
    {
        var restarted = (CoreInfo)instance.GetValue(null)!;
        var expected = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "expected.json")));
        var user = restarted.Users.Single(u => u.ID == 1552239268);
        using var profileDb = GameContext.CreateNew();
        var list = (ResGetFriendData)Call("Get", user, profileDb, now.AddDays(2));
        Check(list.Friend.Count == 30 && user.GetCurrencyVal(CurrencyType.FriendshipPoint) == (long)expected["balance"]!, "restart retains mutual friends and currency");
        var repeated = (ResAllFriendshipPoint)Call("All", user, profileDb, now.AddDays(2));
        Check(repeated.SendCount == 0 && repeated.ReceiveCount == 0, "restart cannot replay daily gifts");
        Console.WriteLine("PASS: separate-process restart retains friendships, gifts and balances without replay");
        return 0;
    }
    var core = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(Path.Combine(fixture, "db.json")))!;
    File.WriteAllText(path, JsonConvert.SerializeObject(core));
    instance.SetValue(null, core);
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted(); db.Database.EnsureCreated();
    foreach (var profile in core.Users)
        db.Users.Add(new GameUser { ID = profile.ID, Nickname = profile.ManagedNpc?.Name ?? "Human" });
    db.SaveChanges();
    User Current(ulong id = 1552239268) => ((CoreInfo)instance.GetValue(null)!).Users.Single(u => u.ID == id);
    long initial = Current().GetCurrencyVal(CurrencyType.FriendshipPoint);
    string originalInventory = JsonConvert.SerializeObject(Current().Characters);
    ulong[] expectedIds = core.Users.Where(u => u.ManagedNpc is { Enabled: true })
        .OrderByDescending(u => u.RepresentationTeamDataNew.Sum(c => (long)FormulaUtils.CalculateCP(u, c)))
        .ThenByDescending(u => u.userPointData.UserLevel).ThenBy(u => u.ID).Take(30).Select(u => u.ID).ToArray();
    var seed = typeof(ManagedNpcService).GetMethod("EnsureNpcFriends");
    Check(seed != null, "one-time strongest NPC friendship initialization is available");
    if (args.Contains("--check-seed-rollback"))
    {
        string beforeSeed = JsonConvert.SerializeObject(instance.GetValue(null));
        using (var blockedSeed = new FileStream(path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            bool failed = false;
            try { seed!.Invoke(null, [1552239268UL, now]); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { failed = true; }
            Check(failed, "friend migration save failure is exercised");
            Check(JsonConvert.SerializeObject(instance.GetValue(null)) == beforeSeed, "failed friend migration restores human initialization flag and both sides of every relation");
        }
        Check((int)seed!.Invoke(null, [1552239268UL, now])! == 30, "failed migration can be retried to create thirty mutual friends");
        var retry = (ResGetFriendData)Call("Get", Current(), db, now);
        Check(retry.Friend.Count == 30, "retried migration has thirty usable mutual friends");
        Console.WriteLine("PASS: seed failure rolls back human and NPC friendship state and retry creates all thirty links");
        return 0;
    }
    Check((int)seed!.Invoke(null, [1552239268UL, now])! == 30, "initialize thirty strongest friends");
    var human = Current();
    var list1 = (ResGetFriendData)Call("Get", human, db, now);
    Check(list1.Friend.Select(f => (ulong)f.User.Usn).ToHashSet().SetEquals(expectedIds), "friends match the actual top thirty combat profiles");
    Check(list1.Friend.All(f => f.User.TeamCombat > 0 && !string.IsNullOrWhiteSpace(f.User.Nickname)), "friend list exposes real profile and combat data");
    Check(list1.SendFriendshipCount == 0 && list1.ReceiveFriendshipCount == 0, "initialization does not spend human daily allowances");
    Check(Current().GetCurrencyVal(CurrencyType.FriendshipPoint) == initial && JsonConvert.SerializeObject(Current().Characters) == originalInventory, "initialization does not grant currency or alter inventory");
    foreach (ulong id in expectedIds)
    {
        var reverse = (ResGetFriendData)Call("Get", Current(id), db, now);
        Check(reverse.Friend.Any(f => f.User.Usn == 1552239268), "friendship is mutual");
    }
    Check((int)seed.Invoke(null, [1552239268UL, now])! == 0, "repeat seed makes no changes");
    Console.WriteLine("PASS: combat-ranked top thirty, mutual real profiles, preserved inventory/currency and idempotent selection");
    if (args.Contains("--check-receipt-replay"))
    {
        var replayFriend = expectedIds[0];
        var originalNonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var firstClaim = (ResObtainFriendshipPoint)Call("Obtain", human, replayFriend, now, originalNonce);
        Check(firstClaim.Result == FriendshipPointResult.Success, "original nonce claims one real gift");
        for (int i = 0; i < 32; i++) Call("Send", human, 0UL, now, BitConverter.GetBytes(i + 100));
        var replay = (ResObtainFriendshipPoint)Call("Obtain", human, replayFriend, now.AddDays(1), originalNonce);
        Check(replay.Result == FriendshipPointResult.Expired && human.GetCurrencyVal(CurrencyType.FriendshipPoint) == initial + 1, "evicted packet cannot claim the next day's gift");
        var fresh = (ResObtainFriendshipPoint)Call("Obtain", human, replayFriend, now.AddDays(1), new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 });
        Check(fresh.Result == FriendshipPointResult.Success && human.GetCurrencyVal(CurrencyType.FriendshipPoint) == initial + 2, "new request can still claim the new day's gift");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "receipt-expected.json"), JsonConvert.SerializeObject(new { friend = replayFriend, balance = initial + 2 }));
        Console.WriteLine("PASS: consumed request cannot replay after 32 newer replies and daily reset; fresh next-day claim remains available");
        return 0;
    }
    if (args.Contains("--check-http"))
    {
        await FriendHttpChecks.Run(human, Path.Combine(AppContext.BaseDirectory, "friends.db"));
        return 0;
    }

    ulong friend = expectedIds[0];
    Check(((ResSendFriendshipPoint)Call("Send", human, friend, now)).Result == FriendshipPointResult.Success, "first gift is sent");
    Check(((ResSendFriendshipPoint)Call("Send", human, friend, now)).Result == FriendshipPointResult.Expired, "same pair cannot send twice per day");
    Check(((ResObtainFriendshipPoint)Call("Obtain", human, friend, now)).Result == FriendshipPointResult.Success, "NPC daily gift can be claimed");
    Check(((ResObtainFriendshipPoint)Call("Obtain", human, friend, now)).Result == FriendshipPointResult.Expired, "same gift cannot be claimed twice");
    Check(human.GetCurrencyVal(CurrencyType.FriendshipPoint) == initial + 1, "single claim grants exactly one point");
    Check(((ResSendFriendshipPoint)Call("Send", human, 99UL, now)).Result == FriendshipPointResult.FriendNotExist, "nonfriend cannot receive gifts");
    var all = (ResAllFriendshipPoint)Call("All", human, db, now);
    Check(all.SendCount == 29 && all.ReceiveCount == 29 && human.GetCurrencyVal(CurrencyType.FriendshipPoint) == initial + 30, "one-click gifts honor thirty-per-day limits and one-point amounts");
    all = (ResAllFriendshipPoint)Call("All", human, db, now);
    Check(all.SendCount == 0 && all.ReceiveCount == 0 && human.GetCurrencyVal(CurrencyType.FriendshipPoint) == initial + 30, "repeated one-click request is harmless");
    Check(db.Triggers.Where(t => t.UserId == human.ID && t.Type == Trigger.SendFriendShipPoint).Sum(t => t.Value) == 30, "mission trigger counts only successful human sends");
    Console.WriteLine("PASS: single and one-click send/obtain, real social currency, daily caps and exact mission events");

    Call("Delete", human, friend, now);
    Check((int)seed.Invoke(null, [human.ID, now])! == 0, "migration does not refill deliberately deleted friends");
    human = Current();
    var friendProperty = typeof(User).GetProperty("Friendships")!;
    void AddRelation(User a, ulong b)
    {
        var state = friendProperty.GetValue(a)!;
        var friends = (HashSet<ulong>)state.GetType().GetProperty("Friends")!.GetValue(state)!;
        friends.Add(b);
    }
    AddRelation(human, friend); AddRelation(Current(friend), human.ID);
    Check(((ResObtainFriendshipPoint)Call("Obtain", human, friend, now)).Result == FriendshipPointResult.Expired, "delete/re-add cannot clear claim history");
    Check(((ResSendFriendshipPoint)Call("Send", human, friend, now)).Result == FriendshipPointResult.Expired, "delete/re-add cannot clear send history");
    all = (ResAllFriendshipPoint)Call("All", human, db, now.AddHours(-1));
    Check(all.SendCount == 0 && all.ReceiveCount == 0, "backward clock does not recreate daily gifts");
    Console.WriteLine("PASS: deleted friends remain deleted after migration; retained pair history prevents re-add and clock exploits");

    var next = now.Date.AddHours(20);
    int received = 0;
    Parallel.For(0, 20, _ => { if (((ResObtainFriendshipPoint)Call("Obtain", human, friend, next)).Result == FriendshipPointResult.Success) Interlocked.Increment(ref received); });
    Check(received == 1 && human.GetCurrencyVal(CurrencyType.FriendshipPoint) == initial + 31, "concurrent claims grant one point exactly across the actual reset boundary");
    all = (ResAllFriendshipPoint)Call("All", human, db, now.AddDays(1));
    Check(all.SendCount == 30 && all.ReceiveCount == 29 && human.GetCurrencyVal(CurrencyType.FriendshipPoint) == initial + 60, "new game day restores exactly one full allowance");
    all = (ResAllFriendshipPoint)Call("All", human, db, now);
    Check(all.SendCount == 0 && all.ReceiveCount == 0, "rollback across day boundary cannot reopen an older day");
    Console.WriteLine("PASS: UTC 20:00 reset, concurrent obtain and cross-day clock rollback");

    human.Currency.Remove(CurrencyType.ContentStamina);
    string beforeFailure = JsonConvert.SerializeObject(instance.GetValue(null));
    using (var blocked = new FileStream(path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        bool failed = false;
        try { Call("All", human, db, now.AddDays(2)); } catch (IOException) { failed = true; }
        Check(failed, "save failure is exercised");
        Check(JsonConvert.SerializeObject(instance.GetValue(null)) == beforeFailure, "save failure restores both sides, counters, currency and mission outbox");
    }
    var giftTask = Task.Run(() => (ResAllFriendshipPoint)Call("All", human, db, now.AddDays(2)));
    var npcTask = Task.Run(() => ManagedNpcService.Configure(true, 1d, now.AddDays(2)));
    await Task.WhenAll(giftTask, npcTask);
    all = giftTask.Result;
    Check(all.SendCount == 30 && all.ReceiveCount == 30 && human.GetCurrencyVal(CurrencyType.FriendshipPoint) == initial + 90, "failed save is safely retryable");
    var persisted = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(path))!;
    Check(persisted.Users.Single(u => u.ID == human.ID).GetCurrencyVal(CurrencyType.FriendshipPoint) == initial + 90 &&
        expectedIds.All(id => persisted.Users.Single(u => u.ID == id).Friendships!.Friends.Contains(human.ID)), "mixed NPC snapshot and gift transaction retains persisted balances and mutual links");
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "expected.json"), JsonConvert.SerializeObject(new { balance = initial + 90 }));
    Console.WriteLine("PASS: atomic save rollback and retry for all friend ledgers, currency and mission notifications");
    Console.WriteLine("PASS: concurrent NPC snapshot update and gift transaction preserve both sides in persisted state");
    return 0;

    object Call(string name, params object[] values)
    {
        try { return service!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == name && m.GetParameters().Length == values.Length).Invoke(null, values)!; }
        catch (TargetInvocationException e) when (e.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
}
catch (Exception e) { Console.WriteLine(e); return 1; }

static void Check(bool success, string message)
{
    if (!success) throw new Exception("FAIL: " + message);
}
