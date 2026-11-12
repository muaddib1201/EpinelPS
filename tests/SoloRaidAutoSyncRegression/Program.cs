using System.Reflection;
using EpinelPS;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Compile and exercise the real source contract before its implementation exists.
// This reflection entry is replaced by typed calls once the source is implemented.
int passed = 0, failed = 0;
Test("NikkeHub structured season uses explicit UTC dates and the inclusive final second", () =>
{
    var source = typeof(User).Assembly.GetType("EpinelPS.LobbyServer.Soloraid.NikkeHubSoloRaidSource")
        ?? throw new InvalidOperationException("Automatic NikkeHub schedule source is not implemented");
    var json = new JObject { ["season"] = new JObject {
        ["id"] = "solo-43", ["kind"] = "solo", ["seasonNo"] = 43,
        ["startAt"] = "2026-11-12T03:00:00.000Z", ["endAt"] = "2026-11-18T19:59:59.000Z",
        ["bosses"] = new JArray(new JObject { ["bossId"] = "victory", ["nameKo"] = "승리의 여신" }) } };
    string model = "1:" + json.ToString(Formatting.None) + "\n";
    string html = "<script>self.__next_f.push(" + JsonConvert.SerializeObject(new object[] { 1, model }) + ")</script>";
    dynamic season = source.GetMethod("ParseHtml")!.Invoke(null, [html])!;
    Check(season.SeasonNo == 43 && season.StartAt == DateTimeOffset.Parse("2026-11-12T03:00:00Z") &&
          season.EndAt == DateTimeOffset.Parse("2026-11-18T20:00:00Z"), "website dates were shifted or the final second was lost");
});
Console.WriteLine($"SoloRaidAutoSync: {passed} PASS, {failed} FAIL");
return failed == 0 ? 0 : 1;

void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
}
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
