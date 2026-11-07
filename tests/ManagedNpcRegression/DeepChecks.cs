using System.Reflection;
using EpinelPS;
using EpinelPS.Controllers;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Services;
using EpinelPS.Utils;
using Newtonsoft.Json;

internal static class DeepChecks
{
    public static async Task Run(Func<CoreInfo> get, Action<CoreInfo> set, GameContext db)
    {
        string baseline = JsonConvert.SerializeObject(get());
        void Reset() => set(JsonConvert.DeserializeObject<CoreInfo>(baseline)!);
        var failures = new List<string>();
        void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name); if (!ok) failures.Add(name); }
        var now = new DateTime(2026, 11, 25, 21, 0, 0, DateTimeKind.Utc);
        try
        {
            // Mutation caught: rescheduling from a backward clock can replay already-awarded days.
            ManagedNpcService.Configure(true, 1, now);
            ManagedNpcService.Tick(now.AddDays(1));
            long actions = get().Users.Sum(u => u.ManagedNpc?.TotalActions ?? 0);
            ManagedNpcService.Configure(true, 2, now.AddDays(-3));
            ManagedNpcService.Tick(now.AddDays(-2));
            Check(get().Users.Sum(u => u.ManagedNpc?.TotalActions ?? 0) == actions, "configuration after a clock rollback cannot replay progress");
            Reset();

            // Mutation caught: NPC-by-NPC catch-up repeatedly resets the same union's daily total.
            ManagedNpcService.Configure(true, 1, now);
            foreach (var p in get().Users.Where(u => u.ManagedNpc != null)) p.ManagedNpc!.Enabled = false;
            int guildId = get().Guilds.Keys.First();
            var pair = get().Users.Where(u => u.ManagedNpc != null && u.Guild.guildId == guildId).Take(2).ToArray();
            for (int i = 0; i < pair.Length; i++)
            {
                var p = pair[i];
                var n = p.ManagedNpc!; n.Enabled = true; n.Tier = 0; n.ActiveHours = 24;
                // Fixed long-interval timelines make the reset-boundary case repeatable.
                n.Seed = i == 0 ? 2037337797 : 1919211488;
                n.Sequence = i == 0 ? 25 : 26;
                n.NextActionUtc = now.AddHours(-25); n.Day = 0; n.ActivityToday = 0;
            }
            get().Guilds[guildId].NpcActivityDay = 0;
            get().Guilds[guildId].Data.ActivityPointDaily = 0;
            // Reset is 20:00 UTC. Give both 20–90 minute timelines more than
            // one maximum interval in the new game day before asserting its total.
            ManagedNpcService.Tick(now.AddHours(2));
            Check(get().Guilds[guildId].Data.ActivityPointDaily == 60, "offline union daily accounting includes both members at the common active rate");
            Reset();

            int beforeCount = get().Users.Count;
            ManagedNpcService.CreateBatch(db, 1, "replay-fixture", now);
            ManagedNpcService.CreateBatch(db, 1, "replay-fixture", now);
            Check(get().Users.Count == beforeCount + 1, "replayed creation form creates one NPC exactly once");
            // Preserve SQL/profile rows for this successful addition in the subsequent fixture baseline.
            baseline = JsonConvert.SerializeObject(get());

            var saveLock = typeof(GameContext).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetField("SaveLock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            using var started = new ManualResetEventSlim();
            Task<string> registration;
            Monitor.Enter(saveLock);
            try
            {
                registration = Task.Run(() =>
                {
                    using var context = GameContext.CreateNew(); started.Set();
                    return new AccountController(context).RegisterAccount("regression", new RegisterEPReq { account = "concurrent-regression", password = "test-only" });
                });
                started.Wait(TimeSpan.FromSeconds(5));
                Thread.Sleep(200);
                Check(!registration.IsCompleted, "human registration waits for the NPC snapshot transaction");
            }
            finally { Monitor.Exit(saveLock); }
            await registration.WaitAsync(TimeSpan.FromSeconds(10));
            Check(db.SdkUsers.Any(u => u.Email == "concurrent-regression") && get().Users.Any(u => u.ID == db.SdkUsers.First(s => s.Email == "concurrent-regression").ID),
                "concurrent registration preserves both game save and SQL identity");

            var human = get().Users.Single(u => u.ID == 1);
            human.Guild = new(); human.userPointData.UserLevel = 40;
            human.Currency[CurrencyType.FreeCash] = 1000;
            int pendingId = 1;
            while (get().Guilds.ContainsKey(pendingId)) pendingId++;
            var pendingUser = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(get().Users.First(u => u.ManagedNpc != null)))!;
            pendingUser.ID = 1_700_000_001; pendingUser.ManagedNpc!.Name = "恢复验证"; pendingUser.ManagedNpc.Seed++;
            pendingUser.Guild = new GuildData { guildId = pendingId, MemberType = 1, JoinedAt = now.Ticks };
            get().NpcManagement!.PendingRequestId = "recovery-fixture";
            get().NpcManagement.PendingUsers.Add(pendingUser);
            get().NpcManagement.PendingGuilds[pendingId] = new SavedGuild { ManagedNpcGuild = true, Data = new NetGuildData { Gsn = pendingId, Name = "恢复联盟", Grade = 1, Emblem = 1, JoinLevel = 10 } };
            var directory = typeof(GameContext).Assembly.GetType("EpinelPS.LobbyServer.Guild.GuildDirectory")!;
            var created = (ResCreateGuild)directory.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                [db, human, new ReqCreateGuild { Name = "真人联盟", Emblem = 1, Locale = "zh-TW" }, null])!;
            Check(created.Guild.Gsn != pendingId, "human union creation reserves pending NPC union IDs");
            if (created.Guild.Gsn != pendingId)
            {
                ManagedNpcService.Recover(db); ManagedNpcService.Recover(db);
                Check(get().Users.Count(u => u.ID == pendingUser.ID) == 1 && db.Users.Count(u => u.ID == pendingUser.ID) == 1,
                    "interrupted creation recovers profiles and membership exactly once");
            }
            if (failures.Count != 0) throw new Exception("Review regressions failed: " + string.Join("; ", failures));
        }
        finally { /* Keep the exercised saves for the separate restart verification process. */ }
    }
}
