using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using EpinelPS;
using EpinelPS.Models;
using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class MuseumLiveProbe
{
    // Called before JsonDb or GameData initialization. This probe never loads
    // the live save into the server's mutable persistence layer.
    public static async Task<int> Run()
    {
        try
        {
            string root = Environment.GetEnvironmentVariable("MUSEUM_LIVE_ROOT")
                ?? throw new InvalidOperationException("MUSEUM_LIVE_ROOT is required.");
            var live = JObject.Parse(File.ReadAllText(Path.Combine(root, "Server and Server selector/db.json")));
            var user = live["Users"]!.OfType<JObject>().First(u => u["ManagedNpc"] is null or { Type: JTokenType.Null });
            ulong id = (ulong)user["ID"]!;
            byte[] key = Convert.FromBase64String((string)live["LauncherTokenKey"]!);
            using var handler = new HttpClientHandler { UseProxy = false,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1/v1/"), Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new PasetoBuilder()
                .Use(ProtocolVersion.V4, Purpose.Local).WithKey(key, Encryption.SymmetricKey).AddClaim("userId", id)
                .Expiration(DateTime.UtcNow.AddMinutes(5)).Encode());
            var archive = ReadArchive();
            int stages = 0, records = 0, squads = 0;
            foreach (var group in archive["Stages"]!.GroupBy(s => (int)s["StageId"]! / 100))
            {
                var response = await Send<ResGetSoloRaidMuseumGroupData>(client, "soloraidmuseum/get/groupdata",
                    new ReqGetSoloRaidMuseumGroupData { GroupId = group.Key });
                foreach (var source in group)
                {
                    int stageId = (int)source["StageId"]!;
                    var stage = response.PastRankingList.Single(s => s.StageId == stageId);
                    Require(stage.TotalUserCount == 50 && stage.RankerRankingList.Count == 5, "live archive count differs");
                    long ownBest = (long?)user["SoloRaidMuseumData"]?[stageId.ToString()]?["Challenge"]?["BestDamage"] ?? 0;
                    long rank = ownBest > 0 ? source["Rankers"]!.Count(r => (long)r["Damage"]! > ownBest) + 1 : 0;
                    Require(stage.UserRanking.CurrentUserData.Usn == (long)id && stage.UserRanking.Damage == ownBest && stage.UserRanking.Ranking == rank,
                        "live personal best changed or profile mismatched");
                    foreach (var row in stage.RankerRankingList)
                    {
                        var expected = source["Rankers"]!.Single(r => (int)r["Ranking"]! == row.Ranking);
                        Require(row.PastUserData.Usn == (long)expected["Usn"]! && row.PastUserData.Usn != (long)id &&
                            row.PastUserData.Nickname == (string)expected["Nickname"]! && row.CurrentUserData.Usn == row.PastUserData.Usn &&
                            row.Damage == (long)source["CommonHp"]! + (long)expected["Damage"]!, "live historical profile or damage differs");
                        var detail = await Send<ResGetSoloRaidMuseumRankerSquad>(client, "soloraidmuseum/get/rankersquad",
                            new ReqGetSoloRaidMuseumRankerSquad { StageId = stageId, TargetUsn = row.PastUserData.Usn });
                        Require(detail.SquadList.Count == 5 && detail.SquadList.Sum(s => s.Damage) == (long)expected["Damage"]!, "live five-team damage differs");
                        foreach (var team in detail.SquadList)
                        {
                            var expectedSquad = expected["Squads"]!.Single(s => (int)s["TeamNumber"]! == team.TeamNumber);
                            var lineup = archive["Lineups"]![(int)expectedSquad["Lineup"]!]!;
                            Require(team.Damage == (long)expectedSquad["Damage"]! && team.Team.Count == 5 &&
                                team.Team.Select(c => c.Tid).SequenceEqual(lineup.Select(c => (int)c["Tid"]!)) &&
                                team.Team.All(c => c.Lv == 400 && c.Combat > 0), "live historical lineup differs");
                            squads++;
                        }
                        records++;
                    }
                    stages++;
                }
            }
            var invalid = await Send<ResGetSoloRaidMuseumRankerSquad>(client, "soloraidmuseum/get/rankersquad",
                new ReqGetSoloRaidMuseumRankerSquad { StageId = 101, TargetUsn = (long)id });
            Require(invalid.SquadList.Count == 0, "live unknown archive target exposes personal data");
            Console.WriteLine(JsonConvert.SerializeObject(new { status = "passed", stages, displayedRecords = records,
                squads, realAccountBattlesSubmitted = 0, clientVisualAcceptance = "pending" }));
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL museum live read-only probe: " + ex.Message); return 1; }
    }

    public static string ArchiveFingerprint()
    {
        var type = typeof(User).Assembly.GetType("EpinelPS.LobbyServer.SoloraidMuseum.MuseumHistoryArchive")!;
        var ranking = type.GetMethod("GetRankings", BindingFlags.Public | BindingFlags.Static)!;
        var squads = type.GetMethod("GetSquads", BindingFlags.Public | BindingFlags.Static)!;
        using var bytes = new MemoryStream();
        foreach (var stage in ReadArchive()["Stages"]!)
        {
            int id = (int)stage["StageId"]!;
            foreach (var row in (IEnumerable<NetSoloRaidMuseumRankerRankingData>)ranking.Invoke(null, [id])!)
            {
                row.WriteDelimitedTo(bytes);
                foreach (var team in (IEnumerable<NetSoloRaidMuseumLog>)squads.Invoke(null, [id, row.PastUserData.Usn])!)
                    team.WriteDelimitedTo(bytes);
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private static JObject ReadArchive()
    {
        using var stream = typeof(User).Assembly.GetManifestResourceStream("EpinelPS.LobbyServer.SoloraidMuseum.MuseumNpcHistory.json")!;
        using var reader = new StreamReader(stream);
        return JObject.Parse(reader.ReadToEnd());
    }

    private static async Task<T> Send<T>(HttpClient client, string route, IMessage request) where T : IMessage<T>, new()
    {
        using var body = new ByteArrayContent(request.ToByteArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
        using var reply = await client.PostAsync(route, body); reply.EnsureSuccessStatusCode();
        T result = new(); result.MergeFrom(await reply.Content.ReadAsByteArrayAsync()); return result;
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
