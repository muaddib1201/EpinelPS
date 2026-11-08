using System.Net.Http.Headers;
using EpinelPS;
using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class RankingLiveProbe
{
    internal static async Task<int> Run()
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("RANKING_LIVE_ROOT") ?? throw new InvalidOperationException("Explicit live root required.");
            var state = JObject.Parse(File.ReadAllText(Path.Combine(root, "Server and Server selector/db.json")));
            var user = state["Users"]!.First(u => u["ManagedNpc"]?.Type is null or JTokenType.Null);
            ulong id = (ulong)user["ID"]!;
            var key = Convert.FromBase64String((string)state["LauncherTokenKey"]!);
            using var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1/v1/ranking/"), Timeout = TimeSpan.FromSeconds(90) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
                .WithKey(key, Encryption.SymmetricKey).AddClaim("userId", id).Expiration(DateTime.UtcNow.AddMinutes(10)).Encode());
            async Task<T> Read<T>(string route, IMessage request) where T : IMessage<T>, new()
            {
                using var content = new ByteArrayContent(request.ToByteArray());
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
                using var response = await client.PostAsync(route, content); response.EnsureSuccessStatusCode();
                T value = new(); value.MergeFrom(await response.Content.ReadAsByteArrayAsync()); return value;
            }
            var overview = await Read<ResGetAllTopRank>("alltoprank", new ReqGetAllTopRank());
            if (overview.TopRankingList.Count != 5) throw new InvalidDataException("Live overview lacks five boards.");
            var boards = new List<object>();
            for (int type = 0; type < 5; type++)
            {
                var board = await Read<ResGetRankingList>("getlist", new ReqGetRankingList { Type = type });
                if (board.User?.User?.Usn != (long)id || board.RankingList.Any(r => r.Rank <= 0 || r.Score <= 0 || r.User == null))
                    throw new InvalidDataException("Live personal row or ranked profiles invalid.");
                var history = await Read<ResGetRankAchievementLeaderBoard>("rankachievement", new ReqGetRankAchievementLeaderBoard { Type = type });
                boards.Add(new { type, entries = board.RankingList.Count, personalRank = board.User.Rank, personalScore = board.User.Score,
                    rewardEnabled = board.RewardEnabled, milestones = history.RankAchievement.Count });
            }
            var counts = await Read<ResGetRankAchievementTop>("rankachievementtop", new ReqGetRankAchievementTop());
            var repeat = await Read<ResGetRankAchievementTop>("rankachievementtop", new ReqGetRankAchievementTop());
            if (counts.TriggerList.Count != 3 || !counts.Equals(repeat)) throw new InvalidDataException("Cumulative counts absent or repeated on refresh.");
            var reward = await Read<ResUpdateRankingServerReward>("updateserverreward", new ReqUpdateRankingServerReward());
            var report = new { status = "passed", utc = DateTime.UtcNow, account = id, boards,
                counts = counts.TriggerList.Select(t => new { trigger = t.Trigger, count = t.UserValue }), hasNewServerReward = reward.HasNewReward };
            string output = Environment.GetEnvironmentVariable("RANKING_PROBE_OUTPUT") ?? throw new InvalidOperationException("Explicit evidence output required.");
            File.WriteAllText(output, JsonConvert.SerializeObject(report, Formatting.Indented));
            Console.WriteLine(JsonConvert.SerializeObject(report)); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL live ranking probe: " + ex); return 1; }
    }
}
