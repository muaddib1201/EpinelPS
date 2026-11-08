using System.Net.Http.Headers;
using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class MissionChecks
{
    private const ulong Player = 1552239268;
    private static readonly string[] PreservedFields = ["Currency", "Items", "Characters", "LostSectorData", "EventMissionInfo",
        "CompletedAchievements", "MainQuestData", "SubQuestData", "StageClearHistorys", "TowerProgress", "UserTeams", "Guild"];

    internal static int Preview(GameData data, string root)
    {
        string live = Path.Combine(root, "Server and Server selector");
        string output = Path.Combine(AppContext.BaseDirectory, "lost-sector-preview.sqlite");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "db.json"), File.ReadAllText(Path.Combine(live, "db.json")));
        using (var source = ReadOnly(Path.Combine(live, "epinelps.db")))
        using (var target = new SqliteConnection($"Data Source={output}"))
        {
            source.Open(); target.Open(); source.BackupDatabase(target);
        }
        GameContext.InitializeOptions($"Data Source={output}", "sqlite");
        var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
        CoreInfo State() => (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
        var before = JObject.FromObject(State());
        void Recover() => typeof(User).Assembly.GetType("EpinelPS.LobbyServer.Mission.MissionProgressDelivery")!
            .GetMethod("RecoverAll", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [DateTime.UtcNow]);
        Recover();
        string settled = JsonConvert.SerializeObject(State());
        Recover();
        Check(settled == JsonConvert.SerializeObject(State()), "repeated repair changed the settled save");
        AssertPreserved(before, JObject.FromObject(State()));
        using var db = GameContext.CreateNew();
        foreach (var user in State().Users)
            foreach (var sector in user.LostSectorData.Where(s => s.Value.ReceivedFinalReward && data.LostSector.ContainsKey(s.Key)))
                Check(db.Triggers.Count(t => t.UserId == user.ID && t.Type == Trigger.LostSectorClear && t.ConditionId == sector.Key && t.Value == 1) == 1,
                    $"clear missing or duplicated for {user.ID}/{sector.Key}");
        Check(db.Triggers.Any(t => t.UserId == Player && t.Type == Trigger.LostSectorClear && t.ConditionId == 1), "real player's clear was not recovered");
        Console.WriteLine("PASS real-save copy: Lost Sector 1 recovered once; repeated recovery unchanged; currencies, inventories, characters, exploration and reward claims preserved");
        return 0;
    }

    internal static async Task<int> Probe(GameData data, string root, string? baselineDirectory)
    {
        string live = Path.Combine(root, "Server and Server selector");
        string save = Path.Combine(live, "db.json");
        var before = JObject.Parse(File.ReadAllText(save));
        var mission = data.DailyEventTable[200010204];
        Check(mission.Trigger == Trigger.LostSectorClear && mission.ConditionId == 1 && mission.ConditionValue == 1, "Day By Day definition changed");
        long clearId;
        using (var db = ReadOnly(Path.Combine(live, "epinelps.db")))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT Id,Value FROM Triggers WHERE UserId=$player AND Type=53 AND ConditionId=1";
            command.Parameters.AddWithValue("$player", (long)Player);
            using var reader = command.ExecuteReader();
            Check(reader.Read() && reader.GetInt32(1) == 1, "live clear is missing");
            clearId = reader.GetInt64(0);
            Check(!reader.Read(), "live clear was duplicated");
        }
        string auth = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
            .WithKey(Convert.FromBase64String(before.Value<string>("LauncherTokenKey")!), Encryption.SymmetricKey)
            .AddClaim("userId", Player).IssuedAt(DateTime.UtcNow).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator })
            { BaseAddress = new Uri("https://127.0.0.1/v1/"), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth);
        var sync = await Send<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger { Seq = clearId - 1 });
        Check(sync.Triggers.Count(t => t.Trigger == 53 && t.ConditionId == 1 && t.UserValue == 1) == 1, "client sync cannot see the repaired clear");
        var sector = (await Send<ResGetLostSectorData>("lostsector/get", new ReqGetLostSectorData())).LostSector.Single(s => s.SectorId == 1);
        Check(sector.IsFinalReward && sector.IsPerfectReward && sector.CurrentClearStageCount == sector.MaxClearStageCount, "live exploration no longer complete");
        var after = JObject.Parse(File.ReadAllText(save));
        Check(JToken.DeepEquals(before, after), "read-only probe changed the saved state");
        if (baselineDirectory != null)
        {
            AssertPreserved(JObject.Parse(File.ReadAllText(Path.Combine(baselineDirectory, "db.json"))), after);
            AssertTriggerHistory(Path.Combine(baselineDirectory, "epinelps.db"), Path.Combine(live, "epinelps.db"), after, data);
        }
        Console.WriteLine($"PASS live Day By Day 200010204: {sector.CurrentClearStageCount}/{sector.MaxClearStageCount} battles, exactly one clear trigger {clearId}, client sync progress 1/1");
        Console.WriteLine("PASS player rewards, event claims and exploration preserved; read-only probe paid no rewards");
        return 0;

        async Task<T> Send<T>(string route, IMessage request) where T : IMessage<T>, new()
        {
            using var body = new ByteArrayContent(request.ToByteArray());
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
            using var response = await http.PostAsync(route, body);
            response.EnsureSuccessStatusCode();
            var result = new T(); result.MergeFrom(await response.Content.ReadAsByteArrayAsync()); return result;
        }
    }

    private static void AssertPreserved(JObject before, JObject after)
    {
        foreach (var user in before["Users"]!)
        {
            var current = after["Users"]!.Single(u => u.Value<ulong>("ID") == user.Value<ulong>("ID"));
            foreach (string field in PreservedFields)
                Check(JToken.DeepEquals(user[field], current[field]), $"repair changed {field} for {user.Value<ulong>("ID")}");
        }
    }

    private static void AssertTriggerHistory(string baseline, string current, JObject save, GameData data)
    {
        var original = Rows(baseline);
        var final = Rows(current);
        Check(original.All(row => final.TryGetValue(row.Key, out var value) && value == row.Value), "existing trigger history changed");
        foreach (var added in final.Where(row => !original.ContainsKey(row.Key)))
        {
            var parts = added.Value.Split('|');
            ulong owner = ulong.Parse(parts[0]); int condition = int.Parse(parts[2]);
            var user = save["Users"]!.Single(u => u.Value<ulong>("ID") == owner);
            Check(parts[1] == "53" && parts[3] == "1" && data.LostSector.ContainsKey(condition) &&
                user["LostSectorData"]?[condition.ToString()]?.Value<bool>("ReceivedFinalReward") == true, "unexpected trigger added at startup");
        }
        Console.WriteLine($"PASS trigger history: {original.Count} existing records preserved, {final.Count - original.Count} proven Lost Sector clear added");
    }

    private static Dictionary<long, string> Rows(string path)
    {
        using var db = ReadOnly(path); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Id,UserId,Type,ConditionId,Value,CreatedAt FROM Triggers ORDER BY Id";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<long, string>();
        while (reader.Read()) rows.Add(reader.GetInt64(0), string.Join('|', Enumerable.Range(1, 5).Select(i => reader.GetValue(i).ToString())));
        return rows;
    }

    private static SqliteConnection ReadOnly(string path) => new(new SqliteConnectionStringBuilder {
        DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
