using System.Reflection;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Utils;
using Newtonsoft.Json;

var root = Environment.GetEnvironmentVariable("NIKKE_WORKSPACE_ROOT")
    ?? throw new InvalidOperationException("Run through the session workflow.");
var output = Console.Out;
Console.SetOut(TextWriter.Null);
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "db.json"), JsonConvert.SerializeObject(new CoreInfo
{
    DbVersion = 5, BondRewardLevelsMigrated = true, LauncherTokenKey = new byte[32],
    EncryptionTokenKey = new byte[32], LogLevel = LogType.Error, ActiveEventBannerIds = [10124]
}));
var pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
Logging.SetOutputLevel(LogType.Error);

int passed = 0;
try
{
    var friend = data.gachaTypes.Values.Single(g => g.Type == GachaPremiumType.GachaFriend);
    var grades = data.GachaGradeProb.Values.Where(g => g.GroupId == friend.GradeProbId).ToArray();
    var allowed = grades.SelectMany(g => Rows(g)).Select(r => r.GachaId).ToHashSet();
    Test("official friendship tables contain the 2/43/55 split and eligible corporations", () =>
    {
        int total = grades.Sum(g => g.Prob);
        foreach (var (rare, percent) in new[] { (OriginalRareType.SSR, 2), (OriginalRareType.SR, 43), (OriginalRareType.R, 55) })
            Check(grades.Where(g => g.Rare == rare).Sum(g => g.Prob) * 100L == total * (long)percent, $"wrong table rate for {rare}");
        Check(allowed.Count > 0 && allowed.All(id => data.CharacterTable[id].Corporation is not (CorporationType.ABNORMAL or CorporationType.PILGRIM)), "ineligible character in fixture");
        output.WriteLine($"FIXTURE banner={friend.Id} characters={allowed.Count} SSR=2% SR=43% R=55%");
    });
    foreach (var grade in grades.Where(g => g.Prob > 0))
        Test($"friendship {grade.Rare} draws only from its official character list", () =>
        {
            var weights = grades.ToDictionary(g => g.Id, g => g.Prob);
            try
            {
                foreach (var row in grades) row.Prob = row.Id == grade.Id ? 10000 : 0;
                var ids = Rows(grade).Select(r => r.GachaId).ToHashSet();
                var drawn = GachaUtils.ExecuteGachaPull(friend, 200, new User());
                Check(drawn.All(c => c.OriginalRare == grade.Rare && ids.Contains(c.Id)), "draw escaped the official list: " + string.Join(",", drawn.Where(c => !ids.Contains(c.Id)).Select(c => c.Id).Distinct().Take(10)));
            }
            finally { foreach (var row in grades) row.Prob = weights[row.Id]; }
        });
    foreach (bool custom in new[] { false, true })
        Test($"friendship uses official rarity weights with custom rates={custom}", () =>
        {
            var u = new User { CustomGachaRates = custom ? new GachaRates { Ssr = 100 } : null };
            var drawn = GachaUtils.ExecuteGachaPull(friend, 10000, u);
            Check(drawn.Count == 10000 && drawn.All(c => allowed.Contains(c.Id)), "draw outside official friendship pool");
            foreach (var (rare, expected, tolerance) in new[] { (OriginalRareType.SSR, 2, 0.8), (OriginalRareType.SR, 43, 2.5), (OriginalRareType.R, 55, 2.5) })
            {
                double percent = 100.0 * drawn.Count(c => c.OriginalRare == rare) / drawn.Count;
                Check(Math.Abs(percent - expected) < tolerance, $"{rare}: expected {expected}%, got {percent:F2}%");
                output.WriteLine($"SAMPLE custom={custom} {rare}={percent:F2}% n={drawn.Count}");
            }
        });
    Test("social selection preserves owned collab characters and account settings", () =>
    {
        var collab = data.CharacterTable.Values.First(c => c.Corporation == CorporationType.ABNORMAL && c.OriginalRare == OriginalRareType.SSR && c.GradeCoreId == 1);
        var u = new User { CustomGachaRates = new GachaRates { Ssr = 20, Sr = 35, R = 45 },
            Characters = [new CharacterModel { Csn = 42, Tid = collab.Id, Level = 123, Grade = 2, Skill1Lvl = 7 }] };
        string before = JsonConvert.SerializeObject(u);
        GachaUtils.ExecuteGachaPull(friend, 10, u);
        Check(JsonConvert.SerializeObject(u) == before, "selection modified existing roster or settings");
    });
    Test("ordinary and pickup recruitment retain custom rarity rates", () =>
    {
        var ordinary = data.gachaTypes.Values.First(g => g.Type == GachaPremiumType.GachaPremium);
        var pickup = data.gachaTypes.Values.First(g => g.Type == GachaPremiumType.GachaPickup);
        foreach (var banner in new[] { ordinary, pickup })
            Check(GachaUtils.ExecuteGachaPull(banner, 100, new User { CustomGachaRates = new GachaRates { Ssr = 100 } })
                .All(c => c.OriginalRare == OriginalRareType.SSR), $"custom rate changed on {banner.Id}");
    });
    output.WriteLine($"{passed} passed, 0 failed");
}
catch (Exception ex)
{
    output.WriteLine($"FAIL after {passed} passed: {ex}");
    Environment.ExitCode = 1;
}
finally { Console.SetOut(output); }

IEnumerable<GachaListProbRecord> Rows(GachaGradeProbRecord grade) =>
    data.GachaListProb.Values.Where(r => r.GroupId == grade.GachaListId && r.Prob > 0);
void Test(string name, Action action) { action(); passed++; output.WriteLine("PASS " + name); }
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
