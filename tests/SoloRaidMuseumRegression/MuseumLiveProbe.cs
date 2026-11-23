using System.Net.Http.Headers;
using EpinelPS.Data;
using Microsoft.Data.Sqlite;
using EpinelPS;
using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class MuseumLiveProbe
{
    // This probe never loads
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
            var names = ReadNames(Path.Combine(root, "Server and Server selector/epinelps.db"));
            var users = live["Users"]!.OfType<JObject>().Where(u =>
                (bool?)u["IsBanned"] != true && names.ContainsKey((long)u["ID"]!)).ToArray();
            int stages = 0, records = 0;
            foreach (var group in GameData.Instance.MuseumStageTable.Values.GroupBy(s => s.GroupId))
            {
                var response = await Send<ResGetSoloRaidMuseumGroupData>(client, "soloraidmuseum/get/groupdata",
                    new ReqGetSoloRaidMuseumGroupData { GroupId = group.Key });
                foreach (var definition in group)
                {
                    string stageKey = definition.Id.ToString();
                    var stage = response.PastRankingList.Single(s => s.StageId == definition.Id);
                    long hp = GameData.Instance.MuseumCommonStageHpGroupTable.Values
                        .Where(h => h.Group == definition.SoloRaidStageHpGroup).Sum(h => h.HpValue);
                    var ranked = users.Select(u => (Id: (long)u["ID"]!, Mode: u["SoloRaidMuseumData"]?[stageKey]?["Challenge"]))
                        .Where(p => (long?)p.Mode?["BestDamage"] > 0)
                        .OrderByDescending(p => (long)p.Mode!["BestDamage"]!).ThenBy(p => p.Id).ToList();
                    Require(stage.TotalUserCount == ranked.Count, "local participant count differs");
                    int own = ranked.FindIndex(p => p.Id == (long)id);
                    Require(stage.UserRanking.CurrentUserData.Usn == (long)id && stage.UserRanking.Ranking == own + 1 &&
                        stage.UserRanking.Damage == (own < 0 ? 0 : WireScore(ranked[own].Mode!, hp)), "personal record differs");
                    foreach (var row in stage.RankerRankingList)
                    {
                        if (row.Ranking > ranked.Count)
                        {
                            Require(row.Damage == 0 && row.PastUserData == null && row.CurrentUserData == null, "vacant rank populated");
                            continue;
                        }
                        var target = ranked[checked((int)row.Ranking - 1)];
                        Require(row.PastUserData.Usn == target.Id && row.CurrentUserData.Usn == target.Id &&
                            row.PastUserData.Nickname == names[target.Id] && row.Damage == WireScore(target.Mode!, hp),
                            "public record or local identity differs");
                        var detail = await Send<ResGetSoloRaidMuseumRankerSquad>(client, "soloraidmuseum/get/rankersquad",
                            new ReqGetSoloRaidMuseumRankerSquad { StageId = definition.Id, TargetUsn = target.Id });
                        var logs = target.Mode!["Logs"]?.OfType<JObject>().ToArray() ?? [];
                        Require(detail.SquadList.Count == logs.Length, "saved squad count differs");
                        foreach (var team in detail.SquadList)
                        {
                            var log = logs.Single(l => (int)l["TeamNumber"]! == team.TeamNumber);
                            Require(team.Damage == (long)log["Damage"]! &&
                                team.Team.Select(c => c.Tid).SequenceEqual(log["Team"]!.Select(c => (int)c["Tid"]!)), "saved squad differs");
                        }
                        records++;
                    }
                    stages++;
                }
            }
            Console.WriteLine(JsonConvert.SerializeObject(new { status = "passed", stages, displayedRecords = records }));
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL museum live read-only probe: " + ex.Message); return 1; }
    }

    private static Dictionary<long, string> ReadNames(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ID, Nickname FROM Users";
        using var reader = command.ExecuteReader();
        var names = new Dictionary<long, string>();
        while (reader.Read()) names.Add(reader.GetInt64(0), reader.GetString(1));
        return names;
    }

    private static long WireScore(JToken mode, long hp) =>
        (long)Math.Min(long.MaxValue, (decimal)(long)mode["BestDamage"]! + hp);

    private static async Task<T> Send<T>(HttpClient client, string route, IMessage request) where T : IMessage<T>, new()
    {
        using var body = new ByteArrayContent(request.ToByteArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
        using var reply = await client.PostAsync(route, body); reply.EnsureSuccessStatusCode();
        T result = new(); result.MergeFrom(await reply.Content.ReadAsByteArrayAsync()); return result;
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
