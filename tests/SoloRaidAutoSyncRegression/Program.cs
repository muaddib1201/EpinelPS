using System.Net;
using System.Reflection;
using EpinelPS;
using EpinelPS.Controllers;
using EpinelPS.Data;
using EpinelPS.Utils;
using EpinelPS.LobbyServer.Soloraid;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
if (root is null) throw new Exception("Session workspace not found");
var data = new GameData(Directory.GetFiles(Path.Combine(root.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var now = DateTimeOffset.Parse("2026-11-13T01:00:00Z");
var current = new RemoteSoloRaidSeason(43, "승리의 여신", DateTimeOffset.Parse("2026-11-12T03:00:00Z"), DateTimeOffset.Parse("2026-11-18T20:00:00Z"));
var future = new RemoteSoloRaidSeason(44, "Future Boss", DateTimeOffset.Parse("2026-12-10T03:00:00Z"), DateTimeOffset.Parse("2026-12-16T20:00:00Z"));
var fallback = new SoloRaidSeasonConfig();
string fixtures = Path.Combine(AppContext.BaseDirectory, "auto-sync-fixtures"); Directory.CreateDirectory(fixtures);
int passed = 0, failed = 0, serial = 0;
if (args.FirstOrDefault(a => a.StartsWith("--probe-source-file=")) is { } probe)
{
    var season = NikkeHubSoloRaidSource.ParseHtml(File.ReadAllText(probe.Split('=', 2)[1]));
    var catalog = Catalog(); catalog.Apply(season, DateTimeOffset.UtcNow);
    var status = catalog.GetStatus(fallback, DateTimeOffset.UtcNow);
    Check(!status.NeedsGameDataUpdate && status.RemoteSeasonNo == 43 && status.CurrentRaidId == 1000044, "Actual source did not match local Boss");
    Console.WriteLine($"PASS actual NikkeHub page: season={season.SeasonNo}, manager={status.CurrentRaidId}, startUtc={season.StartAt:O}, endUtc={season.EndAt:O}"); return 0;
}
await Test("UTC source dates and inclusive final second are preserved", () =>
{
    Check(NikkeHubSoloRaidSource.ParseHtml(Html(current)) == current, "source dates changed"); return Task.CompletedTask;
});
await Test("split model fragments and explicit timezone offsets parse without script execution", () =>
{
    string html = Html(current, true).Replace("2026-11-12T03:00:00.000Z", "2026-11-12T12:00:00+09:00");
    Check(NikkeHubSoloRaidSource.ParseHtml(html) == current, "split or timezone parsing failed"); return Task.CompletedTask;
});
await Test("invalid ambiguous and oversized source fails closed", () =>
{
    foreach (string html in new[] { "<html>maintenance</html>", Html(current).Replace("solo-43", "union-43"), Html(current) + Html(future),
        Html(current).Replace("2026-11-12T03:00:00.000Z", "2026-11-12T03:00:00"), new string('x', NikkeHubSoloRaidSource.MaxHtmlBytes + 1) })
        Throws(() => NikkeHubSoloRaidSource.ParseHtml(html));
    return Task.CompletedTask;
});
await Test("current season matches a complete local preset group", () =>
{
    var catalog = Catalog(); catalog.Apply(current, now);
    Check(catalog.Resolve(fallback, now)!.RaidId == 1000044 && !catalog.GetStatus(fallback, now).NeedsGameDataUpdate, "complete current Boss did not match");
    return Task.CompletedTask;
});
await Test("future Boss activates at the fixed start and survives offline reload", () =>
{
    var (catalog, path) = CatalogWithPath(); catalog.Apply(current, now); catalog.Apply(future, now); using var local = new FutureData(data);
    Check(catalog.Resolve(fallback, future.StartAt.AddTicks(-1))!.RaidId == 1000044 && catalog.Resolve(fallback, future.StartAt)!.RaidId == FutureData.ManagerId, "wrong activation boundary");
    var restarted = new SoloRaidSeasonCatalog(() => data, path);
    Check(restarted.LoadCache() && restarted.Resolve(fallback, future.StartAt)!.EndAt == future.EndAt && restarted.Resolve(fallback, future.StartAt)!.RaidId == FutureData.ManagerId, "offline reload changed the season");
    return Task.CompletedTask;
});
await Test("stale responses and overlapping dates cannot replace the active season", () =>
{
    var catalog = Catalog(); catalog.Apply(current, now); catalog.Apply(future, now); using var local = new FutureData(data);
    catalog.Apply(current, now.AddDays(1));
    Check(catalog.Resolve(fallback, future.StartAt)!.RaidId == FutureData.ManagerId, "stale response rolled back the season");
    Throws(() => catalog.Apply(future with { StartAt = current.StartAt.AddDays(1) }, now));
    Check(catalog.Resolve(fallback, future.StartAt)!.EndAt == future.EndAt, "overlap changed the fixed end"); return Task.CompletedTask;
});
await Test("HTTP and malformed HTML failures preserve the exact durable deadline", async () =>
{
    var (catalog, path) = CatalogWithPath(); catalog.Apply(current, now); string bytes = File.ReadAllText(path);
    using var unavailable = new HttpClient(new Reply(HttpStatusCode.ServiceUnavailable, "")); using var broken = new HttpClient(new Reply(HttpStatusCode.OK, "<html>no data</html>"));
    Check(!await catalog.RefreshAsync(unavailable, now.AddHours(1)) && !await catalog.RefreshAsync(broken, now.AddHours(2)) && File.ReadAllText(path) == bytes && catalog.Resolve(fallback, now.AddDays(1))!.EndAt == current.EndAt, "failed source update changed cache");
});
await Test("refresh reads only the public source without credentials", async () =>
{
    var catalog = Catalog(); var response = new Reply(HttpStatusCode.OK, Html(current)); using var client = new HttpClient(response);
    Check(await catalog.RefreshAsync(client, now) && response.Calls == 1 && catalog.GetStatus(fallback, now).LastSuccessAtUtc == now, "refresh did not persist public data");
});
await Test("failed cache replacement cannot publish a new in-memory season", () =>
{
    var (catalog, path) = CatalogWithPath(); catalog.Apply(current, now); string bytes = File.ReadAllText(path); File.SetAttributes(path, FileAttributes.ReadOnly);
    try { Throws(() => catalog.Apply(future, now)); } finally { File.SetAttributes(path, FileAttributes.Normal); }
    Check(File.ReadAllText(path) == bytes && catalog.GetStatus(fallback, now).RemoteSeasonNo == 43, "failed durable write changed state"); return Task.CompletedTask;
});
await Test("corrupt cache uses fallback and a valid source recovers", async () =>
{
    var (catalog, path) = CatalogWithPath(); File.WriteAllText(path, "{broken");
    Check(!catalog.LoadCache() && catalog.Resolve(fallback, now)!.EndAt == fallback.EndAt, "corrupt cache changed fallback");
    using var client = new HttpClient(new Reply(HttpStatusCode.OK, Html(current)));
    Check(await catalog.RefreshAsync(client, now) && catalog.GetStatus(fallback, now).State == "synced", "valid refresh did not recover");
});
await Test("missing Boss update notice appears and clears when complete data arrives", () =>
{
    var catalog = Catalog(); catalog.Apply(current, now); catalog.Apply(future, now); SoloRaidSeasonSource.Bind(catalog);
    try
    {
        Check(catalog.GetStatus(fallback, now).NeedsGameDataUpdate && catalog.Resolve(fallback, future.StartAt)!.RaidId == 1000044, "missing Boss activated");
        Check(UpdateNotice(), "game notice omitted the requested update prompt"); using var local = new FutureData(data);
        Check(!catalog.GetStatus(fallback, now).NeedsGameDataUpdate && !UpdateNotice(), "update prompt remained after data arrived");
    }
    finally { SoloRaidSeasonSource.Bind(null); }
    return Task.CompletedTask;
});
await Test("battle window follows synchronized season and supports explicit opt-out", () =>
{
    var catalog = Catalog(); catalog.Apply(current, now); catalog.Apply(future, now); SoloRaidSeasonSource.Bind(catalog); using var local = new FutureData(data);
    try
    {
        Check(SoloRaidHelper.GetPeriodResult(FutureData.ManagerId, future.StartAt) == SoloRaidPeriodResult.Success && SoloRaidHelper.GetPeriodResult(1000044, future.StartAt) == SoloRaidPeriodResult.Failure && SoloRaidHelper.GetPeriodResult(FutureData.ManagerId, future.EndAt) == SoloRaidPeriodResult.Failure, "battle still uses manual season only");
        GameConfig.Root.SoloRaidAutoSync = false; Check(SoloRaidSeasonSource.Current(future.StartAt)!.RaidId == 1000044, "explicit opt-out ignored");
    }
    finally { GameConfig.Root.SoloRaidAutoSync = true; SoloRaidSeasonSource.Bind(null); }
    return Task.CompletedTask;
});
Console.WriteLine($"SoloRaidAutoSync: {passed} PASS, {failed} FAIL"); return failed == 0 ? 0 : 1;

SoloRaidSeasonCatalog Catalog() => CatalogWithPath().Catalog;
(SoloRaidSeasonCatalog Catalog, string Path) CatalogWithPath()
{
    string path = Path.Combine(fixtures, $"cache-{++serial}-{Guid.NewGuid():N}.json"); return (new SoloRaidSeasonCatalog(() => data, path), path);
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
}
static bool UpdateNotice() => new LevelInfiniteControlller().GetNotices("test").notice_list.Any(n => n.content_list.Any(c => c.content.Contains("请先更新游戏数据")));
static string Html(RemoteSoloRaidSeason season, bool split = false)
{
    var value = new JObject { ["season"] = new JObject { ["id"] = "solo-" + season.SeasonNo, ["kind"] = "solo", ["seasonNo"] = season.SeasonNo,
        ["startAt"] = season.StartAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ["endAt"] = season.EndAt.AddSeconds(-1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ["bosses"] = new JArray(new JObject { ["bossId"] = "boss", ["nameKo"] = season.BossName }) } };
    string model = "1:" + value.ToString(Formatting.None) + "\n";
    string Frame(string payload) => "<script>self.__next_f.push(" + JsonConvert.SerializeObject(new object[] { 1, payload }) + ")</script>";
    return split ? Frame(model[..80]) + Frame(model[80..]) : Frame(model);
}
static void Throws(Action action) { try { action(); } catch (Exception) { return; } throw new InvalidOperationException("Invalid operation unexpectedly succeeded"); }
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class Reply(HttpStatusCode status, string html) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++;
        if (request.RequestUri?.AbsoluteUri != NikkeHubSoloRaidSource.PageUrl || request.Headers.Authorization is not null || request.Headers.Contains("Cookie")) throw new InvalidOperationException("Source changed or credentials exposed");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"), RequestMessage = request });
    }
}
sealed class FutureData : IDisposable
{
    public const int ManagerId = 240044;
    private readonly GameData data; private readonly int[] ids;
    public FutureData(GameData value)
    {
        data = value; data.SoloRaidManagerTable.Add(ManagerId, new SoloRaidManagerRecord { Id = ManagerId, MonsterPreset = 10044 });
        var copies = data.SoloRaidPresetTable.Values.Where(p => p.PresetGroupId == 10043).ToArray().Select(p =>
        { var copy = JsonConvert.DeserializeObject<SoloRaidPresetRecord>(JsonConvert.SerializeObject(p))!; copy.Id += 1_000_000; copy.PresetGroupId = 10044; return copy; }).ToArray();
        ids = copies.Select(p => p.Id).ToArray(); foreach (var copy in copies) data.SoloRaidPresetTable.Add(copy.Id, copy);
    }
    public void Dispose() { data.SoloRaidManagerTable.Remove(ManagerId); foreach (int id in ids) data.SoloRaidPresetTable.Remove(id); }
}
