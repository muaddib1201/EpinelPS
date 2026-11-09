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
            var npcIds = state["Users"]!.Where(u => u["ManagedNpc"]?.Type is not null and not JTokenType.Null)
                .Select(u => (long)u["ID"]!).ToHashSet();
            var npcCombat = new Dictionary<long, int>();
            void CheckCombat(NetWholeUserData? profile)
            {
                if (profile == null || !npcIds.Contains(profile.Usn)) return;
                if (profile.TeamCombat <= 0) throw new InvalidDataException("Live NPC ranking profile lacks representative team combat: " + profile.Usn);
                npcCombat[profile.Usn] = profile.TeamCombat;
            }
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
            if (!overview.TopRankingList.Select(r => r.Type).Order().SequenceEqual(Enumerable.Range(0, 10)) ||
                overview.TopMuseumTotalRankingList.Count != 1 || overview.TopMuseumGroupRankingList.Count != 3)
                throw new InvalidDataException("Live overview lacks tower or museum cards.");
            foreach (var row in overview.TopRankingList) CheckCombat(row.User);
            foreach (var row in overview.TopMuseumTotalRankingList) CheckCombat(row.User);
            foreach (var row in overview.TopMuseumGroupRankingList) CheckCombat(row.User);
            var boards = new List<object>();
            var firstAchievements = new List<object>();
            void AddFirsts(IEnumerable<NetRankAchievementData> rows, int type, int? group = null)
            {
                foreach (var row in rows)
                {
                    CheckCombat(row.User);
                    if (row.Rank == 1) firstAchievements.Add(new { type, group, target = row.RankId, account = row.User.Usn, score = row.Score, achievedAt = row.AchievedAt });
                }
            }
            for (int type = 0; type < 10; type++)
            {
                var board = await Read<ResGetRankingList>("getlist", new ReqGetRankingList { Type = type });
                if (board.User?.User?.Usn != (long)id || board.RankingList.Any(r => r.Rank <= 0 || r.Score <= 0 || r.User == null))
                    throw new InvalidDataException("Live personal row or ranked profiles invalid.");
                foreach (var row in board.RankingList) CheckCombat(row.User);
                var history = await Read<ResGetRankAchievementLeaderBoard>("rankachievement", new ReqGetRankAchievementLeaderBoard { Type = type });
                AddFirsts(history.RankAchievement, type);
                var top = overview.TopRankingList.Single(r => r.Type == type);
                var first = board.RankingList.FirstOrDefault();
                if (top.Score != (first?.Score ?? 0) || top.User?.Usn != first?.User.Usn)
                    throw new InvalidDataException("Live overview disagrees with ranking detail.");
                boards.Add(new { type, entries = board.RankingList.Count, personalRank = board.User.Rank, personalScore = board.User.Score,
                    rewardEnabled = board.RewardEnabled, milestones = history.RankAchievement.Count });
            }
            var museumTotal = await Read<ResGetMuseumTotalRankingList>("get/museumtotal/rankinglist", new ReqGetMuseumTotalRankingList { Type = 10 });
            if (museumTotal.User?.User?.Usn != (long)id) throw new InvalidDataException("Live museum total profile is absent.");
            foreach (var row in museumTotal.RankingList) CheckCombat(row.User);
            var museumTop = overview.TopMuseumTotalRankingList.Single();
            if (museumTop.Type != 10 || museumTop.Score != (museumTotal.RankingList.FirstOrDefault()?.Score ?? 0) ||
                museumTop.User?.Usn != museumTotal.RankingList.FirstOrDefault()?.User.Usn)
                throw new InvalidDataException("Live museum total overview disagrees with detail.");
            var museumHalls = new List<object>();
            var totalHistory = await Read<ResGetMuseumTotalRankAchievementLeaderBoard>("get/museumtotal/rankachievement", new ReqGetMuseumTotalRankAchievementLeaderBoard { Type = 10 });
            AddFirsts(totalHistory.RankAchievement, 10);
            foreach (var hall in overview.TopMuseumGroupRankingList)
            {
                var detail = await Read<ResGetMuseumGroupRankingList>("get/museumgroup/rankinglist", new ReqGetMuseumGroupRankingList { Type = 11, ConditionId = hall.ConditionId });
                foreach (var row in detail.RankingList) CheckCombat(row.User);
                if (detail.User?.User?.Usn != (long)id || hall.Score != (detail.RankingList.FirstOrDefault()?.Score ?? 0) ||
                    hall.User?.Usn != detail.RankingList.FirstOrDefault()?.User.Usn)
                    throw new InvalidDataException("Live museum hall profile or overview disagrees with detail.");
                museumHalls.Add(new { group = hall.ConditionId, entries = detail.RankingList.Count, personalRank = detail.User.Rank, personalScore = detail.User.Score });
                var history = await Read<ResGetMuseumGroupRankAchievementLeaderBoard>("get/museumgroup/rankachievement", new ReqGetMuseumGroupRankAchievementLeaderBoard { Type = 11, ConditionId = hall.ConditionId });
                AddFirsts(history.RankAchievement, 11, hall.ConditionId);
            }
            var counts = await Read<ResGetRankAchievementTop>("rankachievementtop", new ReqGetRankAchievementTop());
            var repeat = await Read<ResGetRankAchievementTop>("rankachievementtop", new ReqGetRankAchievementTop());
            if (counts.TriggerList.Count != 3 || !counts.Equals(repeat)) throw new InvalidDataException("Cumulative counts absent or repeated on refresh.");
            var reward = await Read<ResUpdateRankingServerReward>("updateserverreward", new ReqUpdateRankingServerReward());
            var towerData = await Read<ResGetTowerData>("../tower/gettowerdata", new ReqGetTowerData());
            var report = new { status = "passed", utc = DateTime.UtcNow, account = id, boards, firstAchievements,
                npcCombat = new { profiles = npcCombat.Count, minimum = npcCombat.Values.DefaultIfEmpty().Min(), maximum = npcCombat.Values.DefaultIfEmpty().Max(),
                    samples = npcCombat.OrderBy(p => p.Key).Take(10).Select(p => new { account = p.Key, combat = p.Value }) },
                towerData = towerData.Data.Select(t => new { type = t.Type, floor = t.Floor, remainCount = t.RemainCount }),
                museumTotal = new { entries = museumTotal.RankingList.Count, personalRank = museumTotal.User.Rank, personalScore = museumTotal.User.Score }, museumHalls,
                counts = counts.TriggerList.Select(t => new { trigger = t.Trigger, count = t.UserValue }), hasNewServerReward = reward.HasNewReward };
            string output = Environment.GetEnvironmentVariable("RANKING_PROBE_OUTPUT") ?? throw new InvalidOperationException("Explicit evidence output required.");
            File.WriteAllText(output, JsonConvert.SerializeObject(report, Formatting.Indented));
            Console.WriteLine(JsonConvert.SerializeObject(report)); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL live ranking probe: " + ex); return 1; }
    }
}
