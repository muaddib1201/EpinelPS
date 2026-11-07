using System.Diagnostics;
using System.Reflection;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

var workspace = new DirectoryInfo(AppContext.BaseDirectory);
while (workspace != null && !Directory.Exists(Path.Combine(workspace.FullName, "src/EpinelPS"))) workspace = workspace.Parent;
if (workspace == null) throw new DirectoryNotFoundException("Cannot locate the NIKKE workspace");
var root = Environment.GetEnvironmentVariable("NIKKE_WORKSPACE_ROOT") ?? workspace.FullName;
var work = Path.Combine(root, ".tools/gacha-selectup-fix-20261107");
var liveDirectory = Path.Combine(root, "Server and Server selector");
if (args.Contains("--probe-live-readonly")) { await ProbeLive(); return; }

var output = Console.Out;
Console.SetOut(TextWriter.Null);
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "db.json"), JsonConvert.SerializeObject(new CoreInfo
{
    DbVersion = 5, BondRewardLevelsMigrated = true, LauncherTokenKey = new byte[32], EncryptionTokenKey = new byte[32], LogLevel = LogType.Error,
    ActiveEventBannerIds = [10124]
}));
var pack = Directory.GetFiles(Path.Combine(liveDirectory, "cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
Logging.SetOutputLevel(LogType.Error);
int failed = 0, passed = 0;

// Removing the per-choice list resolution must make the forced-branch tests fail.
Test("current selectup resolves every configured SSR list", () => CheckMappedChoices(10094));
Test("legacy selectup resolves every configured SSR list", () => CheckMappedChoices(10056));
Test("special selectup resolves every configured SSR list", () => CheckMappedChoices(2001));
Test("different users keep independent lists without mutating the shared grade", () =>
{
    var choices = data.GachaSelectupListTable.Values.Where(c => c.GachaTypeId == 10094 && c.GachaListId > 0).Take(2).ToArray();
    Check(choices.Length == 2 && choices[0].GachaListId != choices[1].GachaListId, "fixture needs two distinct selected lists");
    var grade = data.GachaGradeProb[choices[0].GachaGradeProbId];
    int originalList = grade.GachaListId;
    var first = UserFor(10094, choices[0].Id);
    var second = UserFor(10094, choices[1].Id);
    ForceGrade(10094, grade.Id, () =>
    {
        for (int i = 0; i < 20; i++)
        {
            CheckList(10094, first, choices[0].GachaListId, 10);
            Check(grade.GachaListId == originalList, "first user's choice changed the shared grade");
            CheckList(10094, second, choices[1].GachaListId, 10);
            Check(grade.GachaListId == originalList, "second user's choice changed the shared grade");
        }
    });
});
Test("a choice from another banner cannot supply the selected grade list", () =>
{
    var banner = new GachaTypeRecord { Id = 900000, Type = GachaPremiumType.GachaPickup, GradeProbId = 2148 };
    var u = UserFor(900000, 10501);
    ForceGrade(10094, 214803, () =>
    {
        bool rejected = false;
        try { GachaUtils.ExecuteGachaPull(banner, 1, u); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "a foreign banner's choice supplied characters");
    });
});
Test("selected UP character still follows the saved choice", () =>
{
    foreach (int bannerId in new[] { 10094, 10048, 10078, 10090, 10056, 2001 })
    {
        var banner = data.gachaTypes[bannerId];
        var upGrade = data.GachaGradeProb.Values.Single(g => g.GroupId == banner.GradeProbId
            && data.GachaListProb.Values.Any(r => r.GroupId == g.GachaListId && r.GachaType == GachaCategory.GachaSelectup));
        foreach (var choice in data.GachaSelectupListTable.Values.Where(c => c.GachaTypeId == bannerId))
            ForceGrade(bannerId, upGrade.Id, () => Check(GachaUtils.ExecuteGachaPull(banner, 20, UserFor(bannerId, choice.Id))
                .All(c => c.Id == choice.CharacterId), $"wrong UP character for {bannerId}/{choice.Id}"));
    }
});
Test("custom and official rates survive the selected list resolution", () =>
{
    foreach (int bannerId in new[] { 10094, 10056, 2001 })
        foreach (bool custom in new[] { false, true })
        {
            var choice = data.GachaSelectupListTable.Values.First(c => c.GachaTypeId == bannerId);
            var u = UserFor(bannerId, choice.Id);
            u.CustomGachaRates = custom ? new GachaRates { Ssr = 20, Sr = 35, R = 45 } : null;
            var drawn = GachaUtils.ExecuteGachaPull(data.gachaTypes[bannerId], 10000, u);
            double ssr = 100.0 * drawn.Count(c => c.OriginalRare == OriginalRareType.SSR) / drawn.Count;
            double sr = 100.0 * drawn.Count(c => c.OriginalRare == OriginalRareType.SR) / drawn.Count;
            double r = 100.0 * drawn.Count(c => c.OriginalRare == OriginalRareType.R) / drawn.Count;
            Check(Math.Abs(ssr - (custom ? 20 : 4)) < 3 && Math.Abs(sr - (custom ? 35 : 43)) < 3 && Math.Abs(r - (custom ? 45 : 53)) < 3,
                $"rarity distribution changed for {bannerId}, custom={custom}: {ssr}/{sr}/{r}");
            output.WriteLine($"SAMPLE banner={bannerId} custom={custom} n=10000 SSR={ssr:F2} SR={sr:F2} R={r:F2}");
        }
});
Test("all non-tutorial banner definitions produce characters with saved default choices", () =>
{
    var u = new User { CustomGachaRates = new GachaRates { Ssr = 20, Sr = 35, R = 45 } };
    foreach (var group in data.GachaSelectupListTable.Values.GroupBy(c => c.GachaTypeId))
        u.GachaSelectupChoices[group.Key] = group.OrderByDescending(c => c.IsDefault).ThenBy(c => c.Id).First().Id;
    foreach (var banner in data.gachaTypes.Values.Where(b => b.Type != GachaPremiumType.GachaTutorial))
        Check(GachaUtils.ExecuteGachaPull(banner, 100, u).Count == 100, "wrong pull count for " + banner.Id);
});
Test("tutorial retains exactly one SSR even with custom SSR at 100 percent", () =>
{
    var u = new User { CustomGachaRates = new GachaRates { Ssr = 100 } };
    foreach (var banner in data.gachaTypes.Values.Where(b => b.Type == GachaPremiumType.GachaTutorial))
        for (int i = 0; i < 100; i++)
            Check(GachaUtils.ExecuteGachaPull(banner, 10, u).Count(c => c.OriginalRare == OriginalRareType.SSR) == 1, "tutorial guarantee changed");
});
Console.SetOut(output);
Console.WriteLine($"{passed} passed, {failed} failed");
Environment.ExitCode = failed == 0 ? 0 : 1;

void Test(string name, Action action)
{
    try { action(); passed++; output.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; output.WriteLine("FAIL " + name + ": " + ex); }
}
User UserFor(int banner, int choice) => new() { CustomGachaRates = new GachaRates { Ssr = 100 }, GachaSelectupChoices = new() { [banner] = choice } };
void CheckList(int banner, User u, int list, int count)
{
    var allowed = data.GachaListProb.Values.Where(r => r.GroupId == list && r.Prob > 0).Select(r => r.GachaId).ToHashSet();
    Check(allowed.Count > 0, "empty fixture list");
    var drawn = GachaUtils.ExecuteGachaPull(data.gachaTypes[banner], count, u);
    Check(drawn.Count == count && drawn.All(c => allowed.Contains(c.Id)), "character came from a different selected list");
}
void CheckMappedChoices(int banner)
{
    foreach (var choice in data.GachaSelectupListTable.Values.Where(c => c.GachaTypeId == banner && c.GachaGradeProbId > 0))
    {
        var row = data.GachaGradeProb[choice.GachaGradeProbId];
        int originalList = row.GachaListId;
        ForceGrade(banner, row.Id, () => CheckList(banner, UserFor(banner, choice.Id), choice.GachaListId, 100));
        Check(row.GachaListId == originalList, "selected list leaked to the global table");
    }
}
void ForceGrade(int banner, int grade, Action action)
{
    var rows = data.GachaGradeProb.Values.Where(g => g.GroupId == data.gachaTypes[banner].GradeProbId).ToArray();
    var original = rows.ToDictionary(g => g.Id, g => g.Prob);
    try { foreach (var row in rows) row.Prob = row.Id == grade ? 10000 : 0; action(); }
    finally { foreach (var row in rows) row.Prob = original[row.Id]; }
}
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

async Task ProbeLive()
{
    var savePath = Path.Combine(liveDirectory, "db.json");
    var live = JObject.Parse(File.ReadAllText(savePath));
    const ulong id = 1552239268;
    string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
        .WithKey(Convert.FromBase64String((string)live["LauncherTokenKey"]!), Encryption.SymmetricKey)
        .AddClaim("userId", id).IssuedAt(DateTime.UtcNow).Expiration(DateTime.UtcNow.AddMinutes(10)).Encode();
    var results = new JArray();
    foreach (int banner in new[] { 10094, 10056, 2001, 1, 2, 10092, 10093 })
    {
        var before = State();
        var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-X"); start.ArgumentList.Add("utf8"); start.ArgumentList.Add(Path.Combine(work, "request_live.py"));
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(JsonConvert.SerializeObject(new { token, path = "/adminapi/RunCmd",
            body = new { cmdName = "gacha-sim", p1 = id.ToString(), p2 = $"{banner} 10000" } }));
        process.StandardInput.Close();
        string text = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "HTTPS probe failed: " + error);
        var response = JObject.Parse(text);
        Check((int)response["status"]! == 200, $"banner {banner} returned HTTP {response["status"]}");
        var result = JObject.Parse((string)response["body"]!);
        Check((bool?)result["ok"] == true, "simulation failed: " + result);
        Check(JToken.DeepEquals(before, State()), "simulation changed account resources, characters or recruit counters");
        results.Add(new JObject { ["bannerId"] = banner, ["pulls"] = 10000, ["result"] = result, ["playerGachaStateUnchanged"] = true });
        File.WriteAllText(Path.Combine(work, "live-results.json"), results.ToString());
        Console.WriteLine($"PASS live banner {banner}: {result["error"]}");
    }
    JObject State()
    {
        var u = JObject.Parse(File.ReadAllText(savePath))["Users"]!.Single(u => (ulong)u["ID"]! == id);
        return new JObject(new[] { "CustomGachaRates", "Currency", "Items", "Characters", "GachaBannerMaxPulls", "GachaDailyFreePulls",
            "GachaPaybackData", "GachaPityBannerExecuteCount", "CharacterWishlist", "GachaSelectupChoices", "DailyDiscountUsed" }
            .Select(k => new JProperty(k, u[k]?.DeepClone())));
    }
}
