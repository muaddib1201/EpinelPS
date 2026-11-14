using System.Reflection;
using EpinelPS;
using EpinelPS.Database;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

internal static class SpecialMatchmakingChecks
{
    internal static void VerifySpecialProbe(ResGetSpecialArena special, ResGetSpecialArenaEnemyList rivals, ResGetSpecialArenaTopRank ranking)
    {
        bool hasHigher = ranking.RankList.Any(r => r.Rank > 0 && r.Rank < special.User.Rank);
        var ranks = rivals.List2.Select(r => r.User.Rank).ToArray();
        if (special.IsBan || special.BanInfo != null || special.User.Tier <= 0 || special.User.Rank <= 0 ||
            rivals.List2.Count > 3 || (rivals.List2.Count == 0) == hasHigher ||
            rivals.List2.Any(r => r.RoundCharacter.Count != 15 || r.User.Rank <= 0 || r.User.Rank >= special.User.Rank ||
                r.User.User.Usn == special.User.User.Usn) ||
            rivals.List2.Select(r => r.User.User.Usn).Distinct().Count() != rivals.List2.Count || !ranks.SequenceEqual(ranks.Order()))
            throw new Exception("live special roster invalid");
    }

    internal static async Task Run(Func<string, Func<Task>, Task> test,
        Func<ulong, string, IMessage, string?, Task<HttpResponseMessage>> send, DateTime now, string save)
    {
        var assembly = typeof(User).Assembly;
        var stateProperty = assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
        var original = (CoreInfo)stateProperty.GetValue(null)!;
        var template = original.Users.First(u => u.ManagedNpc != null && u.Characters.Count >= 15 && u.UserTeams.ContainsKey(12));
        ulong[] originalSqlIds;
        using (var db = GameContext.CreateNew()) originalSqlIds = db.Users.Select(u => u.ID).ToArray();
        static void Check(bool ok, string error) { if (!ok) throw new Exception(error); }
        async Task<T> Post<T>(ulong id, string route, IMessage request, string? nonce = null) where T : IMessage<T>, new()
        {
            using var response = await send(id, route, request, nonce);
            Check(response.IsSuccessStatusCode, route + " returned HTTP " + (int)response.StatusCode);
            return new MessageParser<T>(() => new T()).ParseFrom(await response.Content.ReadAsByteArrayAsync());
        }
        Task<ResGetSpecialArenaEnemyList> Rivals(User viewer) => Post<ResGetSpecialArenaEnemyList>(viewer.ID, "arena/special/getenemylist", new ReqGetSpecialArenaEnemyList());
        static long[] Ids(ResGetSpecialArenaEnemyList response) => response.List2.Select(r => r.User.User.Usn).ToArray();
        async Task Fixture(int ownRank, int population, Func<User, User[], Task> body)
        {
            var state = new CoreInfo { Arena = JsonConvert.DeserializeObject<ArenaWorld>(JsonConvert.SerializeObject(original.Arena)) };
            for (int rank = 1; rank <= population; rank++)
            {
                var player = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(template))!;
                player.ID = 10_000_000UL + (ulong)rank; player.IsBanned = false;
                if (rank == ownRank || rank % 2 == 1) player.ManagedNpc = null;
                player.SynchroSlots.Clear();
                foreach (var character in player.Characters) character.Level = rank < ownRank ? 1 : 200;
                player.Arena = new ArenaPlayer { Rating = 1000, RookieJoinedUtc = now, SpecialJoinedUtc = now,
                    SpecialRank = rank, AccruedUtc = now };
                state.Users.Add(player);
            }
            using (var db = GameContext.CreateNew())
            {
                foreach (var player in state.Users)
                    if (!db.Users.Any(u => u.ID == player.ID))
                        db.Users.Add(new GameUser { ID = player.ID, Nickname = "SpecialMatching" + player.ID, LastAction = now });
                db.SaveChanges();
            }
            stateProperty.SetValue(null, state);
            var people = state.Users.ToArray(); var viewer = people[ownRank - 1];
            try
            {
                await Post<ResGetSpecialArena>(viewer.ID, "arena/special/get", new ReqGetSpecialArena());
                await body(viewer, people);
            }
            finally
            {
                stateProperty.SetValue(null, original);
                File.WriteAllText(save, JsonConvert.SerializeObject(original));
                using var db = GameContext.CreateNew();
                db.Users.Where(u => !originalSqlIds.Contains(u.ID)).ExecuteDelete();
            }
        }

        await test("special top three offer only zero, one and two higher opponents", async () =>
        {
            foreach (int rank in new[] { 1, 2, 3 })
                await Fixture(rank, 40, async (viewer, people) =>
                {
                    var response = await Rivals(viewer);
                    var entry = await Post<ResGetSpecialArena>(viewer.ID, "arena/special/get", new ReqGetSpecialArena());
                    var ranking = await Post<ResGetSpecialArenaTopRank>(viewer.ID, "arena/special/toprank", new ReqGetSpecialArenaTopRank());
                    VerifySpecialProbe(entry, response, ranking);
                    if (rank > 1)
                    {
                        void Reject(ResGetSpecialArenaEnemyList invalid)
                        {
                            bool accepted = true;
                            try { VerifySpecialProbe(entry, invalid, ranking); } catch { accepted = false; }
                            Check(!accepted, "live probe accepted an invalid special rival list");
                        }
                        Reject(new ResGetSpecialArenaEnemyList());
                        var lower = response.Clone(); lower.List2[0].User.Rank = rank + 1;
                        Reject(lower);
                        if (rank == 3)
                        {
                            var reversed = response.Clone(); reversed.List2.Clear(); reversed.List2.AddRange(response.List2.Reverse());
                            Reject(reversed);
                        }
                    }
                    Check(response.List2.Count == rank - 1, "top rank offered lower opponents or incorrect target count");
                    Check(response.List2.Select(r => r.User.Rank).SequenceEqual(Enumerable.Range(1, rank - 1).Select(i => (long)i)), "top-rank opponents are not the higher battle ranks");
                    Check(viewer.Arena!.SpecialCount == 0, "empty or refreshed list consumed attempts");
                });
        });
        await test("special challenger targets are fixed and tier transitions use the three higher divisions", async () =>
        {
            await Fixture(10, 40, async (viewer, people) =>
            {
                var first = Ids(await Rivals(viewer));
                Check(first.SequenceEqual(people.Where(p => p.Arena!.SpecialRank is >= 7 and <= 9).Select(p => (long)p.ID)), "challenger ten did not receive ranks seven to nine");
                var refreshed = Ids(await Rivals(viewer));
                Check(first.SequenceEqual(refreshed), "refresh replaced single-member challenger divisions");
            });
            await Fixture(16, 40, async (viewer, people) =>
            {
                var response = await Rivals(viewer);
                Check(response.List2.Count == 3 && response.List2[0].User.Rank == 9 && response.List2[1].User.Rank == 10 &&
                    response.List2[2].User.Rank is >= 11 and <= 15, "diamond boundary did not use the actual higher tier ranges");
            });
            await Fixture(11, 40, async (viewer, people) =>
            {
                var response = await Rivals(viewer);
                Check(response.List2.Select(r => r.User.Rank).SequenceEqual(new long[] { 8, 9, 10 }), "first diamond division did not offer challenger eight to ten");
            });
        });
        await test("special refresh covers all members of higher divisions without power or NPC preference", () => Fixture(31, 42, async (viewer, people) =>
        {
            var seen = new HashSet<long>(); long[]? first = null;
            for (int i = 0; i < 10; i++)
            {
                var response = await Rivals(viewer); first ??= Ids(response);
                var ranks = response.List2.Select(r => r.User.Rank).ToArray();
                Check(ranks.Length == 3 && ranks[0] is >= 11 and <= 15 && ranks[1] is >= 16 and <= 20 && ranks[2] is >= 21 and <= 30,
                    "rivals escaped the higher divisions or were excluded by combat power");
                Check(Ids(response).Distinct().Count() == 3 && response.List2.All(r => r.RoundCharacter.Count == 15), "duplicate rival or invalid three-team snapshot");
                seen.UnionWith(Ids(response));
            }
            Check(people.Where(p => p.Arena!.SpecialRank is >= 11 and <= 30).All(p => seen.Contains((long)p.ID)), "a human or NPC in a higher division remained unreachable");
            foreach (var person in people) foreach (var character in person.Characters) character.Level = 100;
            viewer.Arena!.SpecialRefresh = 0;
            var afterPowerChange = Ids(await Rivals(viewer));
            Check(first!.SequenceEqual(afterPowerChange), "combat power changed special matchmaking");
        }));
        await test("special sparse groups remain upward and filter banned, locked and malformed defenses", () => Fixture(31, 42, async (viewer, people) =>
        {
            foreach (var person in people.Where(p => p != viewer && p.Arena!.SpecialRank is not (2 or 13 or 26))) person.IsBanned = true;
            var sparse = await Rivals(viewer);
            Check(sparse.List2.Select(r => r.User.Rank).SequenceEqual(new long[] { 2, 13, 26 }), "sparse ladder lost higher targets or filled with lower ranks");
            people[1].LastNormalStageCleared = 0; people[1].userPointData.UserLevel = 1;
            people[12].UserTeams[12].Teams[0].Slots.RemoveAt(0);
            var remaining = await Rivals(viewer);
            Check(remaining.List2.Count == 1 && remaining.List2[0].User.Rank == 26, "locked or malformed defense appeared in the special list");
            viewer.Arena!.SpecialRank = 30;
            var sameTier = await Rivals(viewer);
            Check(sameTier.List2.Count == 1 && sameTier.List2[0].User.Rank == 26, "same-tier sparse fallback offered a lower rank or lost the only higher target");
        }));
        await test("special refresh is isolated from rookie lists and survives legacy cursor bounds", () => Fixture(31, 42, async (viewer, people) =>
        {
            var rookie = await Post<ResGetArenaEnemyList>(viewer.ID, "arena/getenemylist", new ReqGetArenaEnemyList());
            var expected = rookie.List2.Select(r => r.User.User.Usn).ToArray();
            viewer.Arena!.RookieRefresh = 0;
            foreach (long cursor in new[] { -1L, long.MaxValue })
            {
                viewer.Arena.SpecialRefresh = cursor;
                var response = await Rivals(viewer);
                Check(response.List2.Count == 3 && response.List2.All(r => r.User.Rank < viewer.Arena.SpecialRank) && viewer.Arena.SpecialRefresh >= 0,
                    "special cursor overflow or legacy value broke upward matching");
            }
            Check(viewer.Arena.RookieRefresh == 0 && viewer.Arena.SpecialCount == 0 && viewer.Arena.RookieCount == 0, "special refresh changed rookie cursor or attempts");
            var actual = (await Post<ResGetArenaEnemyList>(viewer.ID, "arena/getenemylist", new ReqGetArenaEnemyList())).List2.Select(r => r.User.User.Usn).ToArray();
            Check(expected.SequenceEqual(actual), "special matching changed rookie opponent selection");
            var persisted = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))!;
            Check(persisted.Users.Single(p => p.ID == viewer.ID).Arena!.SpecialRefresh == viewer.Arena.SpecialRefresh, "special cursor was not persisted");
        }));
        await test("special new starts reject lower and invalid ranks before consuming attempts or tickets", () => Fixture(16, 40, async (viewer, people) =>
        {
            viewer.Arena!.SpecialCount = 2; viewer.Currency[EpinelPS.Data.CurrencyType._2nd_Arena_Ticket] = 1;
            foreach (long rank in new[] { 20L, 16L, 0L })
            {
                people[19].Arena!.SpecialRank = rank;
                string before = JsonConvert.SerializeObject(stateProperty.GetValue(null));
                using var response = await send(viewer.ID, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = (long)people[19].ID }, null);
                Check((int)response.StatusCode == 409, "non-upward special battle was accepted");
                Check(JsonConvert.SerializeObject(stateProperty.GetValue(null)) == before, "rejected target consumed a ticket or changed player state");
            }
        }));
        await test("special pending starts retain their snapshot when the defender later drops below the attacker", () => Fixture(16, 40, async (viewer, people) =>
        {
            var target = people[10];
            var first = await Post<ResSpecialArenaStart>(viewer.ID, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = (long)target.ID });
            (target.Arena!.SpecialRank, people[19].Arena!.SpecialRank) = (people[19].Arena!.SpecialRank, target.Arena.SpecialRank);
            var retry = await Post<ResSpecialArenaStart>(viewer.ID, "arena/special/start", new ReqSpecialArenaStart { EnemyUsn = (long)target.ID });
            Check(first.Equals(retry) && viewer.Arena!.SpecialCount == 1, "a pending retry was invalidated or charged again after a rank change");
        }));
        await test("NPC special battles use upward groups and the leader does not spend attempts without a target", () => Fixture(16, 40, async (viewer, people) =>
        {
            viewer.ManagedNpc = JsonConvert.DeserializeObject<ManagedNpcState>(JsonConvert.SerializeObject(template.ManagedNpc))!;
            foreach (var person in people.Where(p => p.ManagedNpc != null)) person.ManagedNpc!.Enabled = true;
            viewer.Arena!.RookieCount = 5;
            int Run()
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var scopeType = assembly.GetType("EpinelPS.Utils.RewardMutationScope")!;
                var serviceType = assembly.GetType("EpinelPS.LobbyServer.Arena.ArenaService")!;
                using var scope = (IDisposable)Activator.CreateInstance(scopeType, flags, null, [viewer, now], null)!;
                var service = Activator.CreateInstance(serviceType, flags, null, [viewer, now, null], null)!;
                return (int)serviceType.GetMethod("RunNpcRoutine", flags)!.Invoke(service, null)!;
            }
            Check(Run() == 2, "NPC did not complete available special challenges");
            foreach (var record in viewer.Arena.SpecialRecords.Select(NetSpecialArenaRecordData.Parser.ParseFrom))
            {
                var target = people.Single(p => p.ID == (ulong)record.User.Usn);
                var defense = target.Arena!.SpecialRecords.Select(NetSpecialArenaRecordData.Parser.ParseFrom).Single(r => r.MatchId == record.MatchId);
                Check(target.ManagedNpc != null && defense.BeforeRank < record.BeforeRank && record.BeforeRank >= record.AfterRank,
                    "NPC attacked a human or selected a lower battle rank");
            }
            viewer.Arena.SpecialRank = 1; viewer.Arena.SpecialCount = 0; viewer.Arena.SpecialRecords.Clear();
            Check(Run() == 0 && viewer.Arena.SpecialCount == 0 && viewer.Arena.SpecialRecords.Count == 0, "top NPC wasted special attempts on lower ranks");
            await Task.CompletedTask;
        }));
    }
}
