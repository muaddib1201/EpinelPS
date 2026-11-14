using System.Reflection;
using EpinelPS;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Utils;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class RookieMatchmakingChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> test,
        Func<ulong, Task<ResGetArenaEnemyList>> list, Func<ulong, Task<ResGetArena>> entry,
        Func<ulong, Task<ResGetSpecialArenaEnemyList>> special, DateTime now, string save)
    {
        var stateProperty = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
        var original = (CoreInfo)stateProperty.GetValue(null)!;
        var template = original.Users.First(u => u.ManagedNpc != null && u.Characters.Count >= 15 && u.UserTeams.ContainsKey(12));

        async Task Fixture(int opponents, Func<User[], Task> body)
        {
            var state = new CoreInfo { Arena = JsonConvert.DeserializeObject<ArenaWorld>(JsonConvert.SerializeObject(original.Arena)) };
            for (int i = 0; i <= opponents; i++)
            {
                var player = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(template))!;
                player.ID = 9_000_000UL + (ulong)i; player.IsBanned = false;
                if (i == 0 || i % 2 == 1) player.ManagedNpc = null;
                player.SynchroSlots.Clear();
                foreach (var character in player.Characters) character.Level = i is >= 1 and <= 9 ? 1 : 200;
                player.Arena = new ArenaPlayer { Rating = i == 0 ? 1807 : i <= 9 ? 1820 : 1600,
                    RookieJoinedUtc = now, SpecialJoinedUtc = now, SpecialRank = i + 1, AccruedUtc = now };
                state.Users.Add(player);
            }
            using (var db = GameContext.CreateNew())
            {
                foreach (var player in state.Users)
                    if (!db.Users.Any(u => u.ID == player.ID))
                        db.Users.Add(new GameUser { ID = player.ID, Nickname = "Matching" + player.ID, LastAction = now });
                db.SaveChanges();
            }
            stateProperty.SetValue(null, state);
            try { await entry(state.Users[0].ID); await body(state.Users.ToArray()); }
            finally
            {
                stateProperty.SetValue(null, original);
                File.WriteAllText(save, JsonConvert.SerializeObject(original));
            }
        }
        static void Check(bool ok, string error) { if (!ok) throw new Exception(error); }
        static long[] Ids(ResGetArenaEnemyList result) => result.List2.Select(r => r.User.User.Usn).ToArray();

        await test("rookie refresh reaches every higher-ranked human and NPC despite a combat-power gap", () => Fixture(30, async players =>
        {
            int Combat(User player) => player.UserTeams[3].Teams.SelectMany(t => t.Slots).Sum(s => FormulaUtils.CalculateCP(player, s.Value));
            Check(Combat(players[0]) > Combat(players[1]) * 2, "fixture must reproduce a large power difference");
            var higher = players.Skip(1).Take(9).Select(p => (long)p.ID).ToHashSet();
            var seen = new HashSet<long>();
            for (int i = 0; i < higher.Count; i++)
            {
                var result = await list(players[0].ID);
                Check(Ids(result).Any(higher.Contains), "higher-ranked opponents excluded by combat-power matching");
                seen.UnionWith(Ids(result));
            }
            Check(higher.IsSubsetOf(seen), "refresh did not reach every higher-ranked opponent");
        }));
        await test("rookie lists contain distinct higher, nearest-score and lower candidates in descending score order", () => Fixture(30, async players =>
        {
            var result = await list(players[0].ID);
            Check(result.List2.Count == 3 && Ids(result).Distinct().Count() == 3 && !Ids(result).Contains((long)players[0].ID), "duplicate or self opponent");
            Check(result.List2.Any(r => r.User.Rating > 1807) && result.List2.Any(r => r.User.Rating < 1807), "difficulty roles missing");
            Check(result.List2.Count(r => r.User.Rating == 1820) == 2, "nearest remaining rating cohort was not offered");
            Check(result.List2.Select(r => r.User.Rating).SequenceEqual(result.List2.Select(r => r.User.Rating).OrderDescending()), "rookie list is not score-descending");
        }));
        await test("rookie matching ignores combat changes and does not truncate tied peers to twelve", () => Fixture(30, async players =>
        {
            var expected = Ids(await list(players[0].ID));
            players[0].Arena!.RookieRefresh = 0;
            foreach (var player in players)
                foreach (var character in player.Characters) character.Level = 100;
            Check(expected.SequenceEqual(Ids(await list(players[0].ID))), "combat changes changed rookie opponents");
            foreach (var player in players) player.Arena!.Rating = 1000;
            // Put the viewer last in the existing ID tie order, so all thirty peers are higher-ranked.
            players[0].ID = 9_000_100;
            using (var db = GameContext.CreateNew())
            {
                if (!db.Users.Any(u => u.ID == players[0].ID))
                    db.Users.Add(new GameUser { ID = players[0].ID, Nickname = "MatchingTies", LastAction = now });
                db.SaveChanges();
            }
            players[0].Arena!.RookieRefresh = 0;
            var seen = new HashSet<long>();
            for (int i = 0; i < 30; i++) seen.UnionWith(Ids(await list(players[0].ID)));
            Check(players.Skip(1).All(p => seen.Contains((long)p.ID)), "equal-score opponents beyond twelve remained unreachable");
        }));
        await test("rookie matching fills small populations and top or bottom ranks without duplicates", async () =>
        {
            foreach (int count in new[] { 0, 1, 2, 5 })
                await Fixture(count, async players =>
                {
                    foreach (int rating in new[] { int.MinValue, int.MaxValue, 1820 })
                    {
                        players[0].Arena!.Rating = rating;
                        var result = await list(players[0].ID);
                        Check(result.List2.Count == Math.Min(3, count) && Ids(result).Distinct().Count() == result.List2.Count, "small population or boundary rank lost/duplicated opponents");
                    }
                });
        });
        await test("rookie refresh excludes banned, locked and invalid defense teams", () => Fixture(30, async players =>
        {
            players[1].IsBanned = true;
            players[2].LastNormalStageCleared = 0; players[2].userPointData.UserLevel = 1;
            players[3].UserTeams[3].Teams[0].Slots.RemoveAt(0);
            var unavailable = players.Take(4).Select(p => (long)p.ID).ToHashSet();
            for (int i = 0; i < 12; i++)
                Check(!Ids(await list(players[0].ID)).Any(unavailable.Contains), "ineligible participant was offered");
        }));
        await test("rookie refresh preserves attempts, other player data and special matching while persisting its cursor", () => Fixture(30, async players =>
        {
            var human = players[0];
            human.Arena!.SpecialRefresh = 0;
            var expectedSpecial = (await special(human.ID)).List2.Select(r => r.User.User.Usn).ToArray();
            human.Arena.SpecialRefresh = 0;
            var before = JObject.FromObject(human); ((JObject)before["Arena"]!).Remove("RookieRefresh");
            var others = JsonConvert.SerializeObject(players.Skip(1));
            long cursor = human.Arena.RookieRefresh;
            await list(human.ID);
            Check(human.Arena.RookieRefresh == cursor + 1 && human.Arena.SpecialRefresh == 0, "mode cursors interfered");
            var after = JObject.FromObject(human); ((JObject)after["Arena"]!).Remove("RookieRefresh");
            Check(JToken.DeepEquals(before, after) && others == JsonConvert.SerializeObject(players.Skip(1)), "refresh changed attempts, rewards, teams or other progress");
            var persisted = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))!;
            Check(persisted.Users.Single(u => u.ID == human.ID).Arena!.RookieRefresh == cursor + 1, "refresh cursor was not saved");
            Check(expectedSpecial.SequenceEqual((await special(human.ID)).List2.Select(r => r.User.User.Usn)), "rookie change altered special opponents");
        }));
        await test("rookie refresh accepts legacy negative and exhausted counters", () => Fixture(5, async players =>
        {
            foreach (long cursor in new[] { -1L, long.MaxValue })
            {
                players[0].Arena!.RookieRefresh = cursor;
                Check((await list(players[0].ID)).List2.Count == 3 && players[0].Arena!.RookieRefresh >= 0, "legacy cursor caused an invalid list or overflow");
            }
        }));
    }
}
