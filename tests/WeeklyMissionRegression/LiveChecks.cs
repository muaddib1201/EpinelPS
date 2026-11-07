using System.Net.Http.Headers;
using EpinelPS;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class WeeklyMissionLiveChecks
{
    internal static async Task<int> Run(string root)
    {
        try
        {
            string livePath = Path.Combine(root, "Server and Server selector/db.json");
            var live = JObject.Parse(File.ReadAllText(livePath));
            var human = live["Users"]!.Cast<JObject>().Single(u => u["ManagedNpc"] == null);
            ulong id = (ulong)human["ID"]!;
            if ((int?)human["TriggerSyncClockVersion"] is not >= 1)
                throw new Exception("The real client still has a pending trigger-history refresh; do not consume it in a probe.");
            if (human["PendingMissionTriggers"] is JArray { Count: > 0 })
                throw new Exception("Pending mission notifications must be delivered before a read-only sync probe.");
            string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
                .WithKey(Convert.FromBase64String((string)live["LauncherTokenKey"]!), Encryption.SymmetricKey)
                .AddClaim("userId", id).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1/v1/") };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            async Task<T> Read<T>(string route, IMessage request) where T : IMessage<T>, new()
            {
                using var content = new ByteArrayContent(request.ToByteArray());
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
                using var response = await client.PostAsync(route, content);
                response.EnsureSuccessStatusCode();
                return new MessageParser<T>(() => new()).ParseFrom(await response.Content.ReadAsByteArrayAsync());
            }
            var claimed = await Read<ResGetRewardedData>("mission/getrewarded/all", new ReqGetRewardedData());
            if (!claimed.WeeklyIds.Order().SequenceEqual(human["WeeklyResetableData"]!["CompletedWeeklyMissions"]!.Values<int>().Order()))
                throw new Exception("The mission API did not preserve weekly reward claims.");
            var all = new Dictionary<long, NetTrigger>();
            long seq = 0;
            for (int page = 0; ; page++)
            {
                if (page >= 100) throw new Exception("Mission history paging did not finish.");
                var response = await Read<ResSyncTrigger>("trigger/sync", new ReqSyncTrigger { Seq = seq, IsTrustedClientSeq = true });
                if (response.Restart) throw new Exception("The probe unexpectedly consumed a history refresh.");
                foreach (var trigger in response.Triggers) all[trigger.Seq] = trigger;
                if (!response.HasRemainData) break;
                long next = response.Triggers.Select(t => t.Seq).DefaultIfEmpty(seq).Max();
                if (next <= seq) throw new Exception("Mission history paging did not advance.");
                seq = next;
            }
            var now = DateTime.UtcNow;
            var start = now.Date.AddDays(-((int)now.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7)
                .AddHours((int)live["ResetHourUtcTime"]!);
            if (start > now) start = start.AddDays(-7);
            var types = new[] { 13, 71, 45 };
            var expected = types.ToDictionary(t => t, _ => 0L);
            using (var sql = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(root, "Server and Server selector/epinelps.db"), Mode = SqliteOpenMode.ReadOnly }.ToString()))
            {
                await sql.OpenAsync();
                using var command = sql.CreateCommand();
                command.CommandText = "SELECT Type,SUM(Value) FROM Triggers WHERE UserId=$user AND ConditionId=0 AND CreatedAt >= $start AND CreatedAt < $end AND Type IN (13,71,45) GROUP BY Type";
                command.Parameters.AddWithValue("$user", checked((long)id));
                command.Parameters.AddWithValue("$start", start.AddHours(9).Ticks);
                command.Parameters.AddWithValue("$end", start.AddDays(7).AddHours(9).Ticks);
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) expected[reader.GetInt32(0)] = reader.GetInt64(1);
            }
            foreach (int type in types)
            {
                long actual = all.Values.Where(t => t.Trigger == type && t.ConditionId == 0 && t.CreatedAt >= start.Ticks &&
                    t.CreatedAt < start.AddDays(7).Ticks).Sum(t => (long)t.UserValue);
                if (actual != expected[type]) throw new Exception($"Mission type {type} differs between SQLite ({expected[type]}) and client sync ({actual}).");
            }
            var after = JObject.Parse(File.ReadAllText(livePath))["Users"]!.Cast<JObject>().Single(u => (ulong)u["ID"]! == id);
            foreach (string field in new[] { "Currency", "Items", "Characters", "WeeklyResetableData" })
                if (!JToken.DeepEquals(human[field], after[field])) throw new Exception("The read-only probe changed " + field);
            Console.WriteLine($"PASS: live weekly trigger sync matches recruit={expected[13]}, upgrade={expected[71]}, gifts={expected[45]}");
            Console.WriteLine("PASS: live weekly reward claims, characters, inventory and currencies were preserved");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: live weekly probe: " + error.Message);
            return 1;
        }
    }
}
