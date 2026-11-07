using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;
using Newtonsoft.Json;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    internal static T Clone<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))!;

    public static int Create(GameContext db, int count, DateTime now)
        => CreateBatch(db, count, Guid.NewGuid().ToString("N"), now);

    public static int EnsureInitial(GameContext db, DateTime now)
    {
        lock (JsonDb.SaveLock)
        {
            Recover(db);
            return JsonDb.Instance.Users.Any(u => u.ManagedNpc != null) ? 0 : CreateBatch(db, 30, "initial-30", now);
        }
    }

    public static int EnsurePopulation(GameContext db, int target, DateTime now)
    {
        if (target is < 1 or > 200) throw new ArgumentException("NPC 目标人数必须在 1～200 之间。");
        lock (JsonDb.SaveLock)
        {
            Recover(db);
            int missing = target - JsonDb.Instance.Users.Count(u => u.ManagedNpc != null);
            return missing > 0 ? CreateBatch(db, missing, $"population-{target}-{Guid.NewGuid():N}", now) : 0;
        }
    }

    public static void UpgradeBreakthroughs()
    {
        lock (JsonDb.SaveLock)
        {
            if (!JsonDb.Instance.Users.Any(ManagedNpcBreakthroughs.NeedsRepair)) return;
            var settings = JsonDb.Instance.NpcManagement;
            Change(() =>
            {
                // This repair must not create settings that enable a previously paused manager.
                JsonDb.Instance.NpcManagement = settings;
                foreach (var user in JsonDb.Instance.Users.Where(u => u.ManagedNpc != null))
                    ManagedNpcBreakthroughs.Apply(user);
            });
        }
    }

    public static void UpgradeGrowth()
    {
        lock (JsonDb.SaveLock)
        {
            if (!JsonDb.Instance.Users.Any(u => u.ManagedNpc is { GrowthSchemaVersion: < ManagedNpcGrowth.SchemaVersion })) return;
            var settings = JsonDb.Instance.NpcManagement;
            Change(() =>
            {
                JsonDb.Instance.NpcManagement = settings;
                foreach (var user in JsonDb.Instance.Users.Where(u => u.ManagedNpc is { GrowthSchemaVersion: < ManagedNpcGrowth.SchemaVersion }))
                    ManagedNpcGrowth.Apply(user);
            });
        }
    }

    public static void UpgradeRosters(DateTime now)
    {
        lock (JsonDb.SaveLock)
        {
            if (!JsonDb.Instance.Users.Any(u => u.ManagedNpc is { TeamSchemaVersion: < ManagedNpcRoster.SchemaVersion })) return;
            Change(() =>
            {
                foreach (var user in JsonDb.Instance.Users.Where(u => u.ManagedNpc is { TeamSchemaVersion: < ManagedNpcRoster.SchemaVersion }))
                    ManagedNpcRoster.Apply(user);
            });
        }
    }

    public static int CreateBatch(GameContext db, int count, string requestId, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64) throw new ArgumentException("创建批次无效，请刷新页面。");
        lock (JsonDb.SaveLock)
        {
            Recover(db);
            if (JsonDb.Instance.NpcManagement?.CreatedBatches.ContainsKey(requestId) == true) return 0;
            int existing = JsonDb.Instance.Users.Count(u => u.ManagedNpc != null);
            if (count < 1 || existing + count > 200) throw new ArgumentException("NPC 总数必须在 1～200 之间。");
            var settings = Clone(JsonDb.Instance.NpcManagement ?? new());
            settings.PendingRequestId = requestId;
            var reserved = JsonDb.Instance.Users.Select(u => u.ID).Concat(db.Users.Select(u => u.ID))
                .Concat(db.SdkUsers.Select(u => u.ID)).ToHashSet();
            ulong uid = 1_500_000_000;
            for (int i = 0; i < count; i++)
            {
                while (reserved.Contains(uid)) uid++;
                settings.PendingUsers.Add(ManagedNpcFactory.Create(uid, existing + i, now));
                reserved.Add(uid++);
            }
            var guilds = JsonDb.Instance.Guilds.Values.Where(g => g.ManagedNpcGuild).Select(g => g.Data.Gsn).ToList();
            if (guilds.Count == 0)
            {
                var names = DefaultGuildNames;
                int nextId = 1;
                for (int i = 0; i < Math.Min(names.Length, count); i++)
                {
                    while (JsonDb.Instance.Guilds.ContainsKey(nextId) || JsonDb.Instance.Users.Any(u => u.Guild.guildId == nextId)) nextId++;
                    string name = names[i];
                    int suffix = 2;
                    while (JsonDb.Instance.Guilds.Values.Any(g => g.Data.Name == name)) name = names[i] + suffix++;
                    var guild = new SavedGuild
                    {
                        ManagedNpcGuild = true,
                        Data = new NetGuildData { Gsn = nextId, Name = name, Desc = "一起慢慢成长，欢迎指挥官。", Emblem = 1,
                            Locale = "zh-TW", Grade = 1, JoinType = 0, JoinLevel = 10 },
                        CreationLog = new NetGuildLog { Seq = 1, Id = "GUILD_JOURNAL_GUILD_CREATE", CreatedAt = now.Ticks,
                            Arguments = { new NetGuildLogArgs { Value = settings.PendingUsers[i].ManagedNpc!.Name } } }
                    };
                    settings.PendingGuilds.Add(nextId, guild); guilds.Add(nextId++);
                }
                // Eight members per union, interleaved so all founders exist even in small custom batches.
                for (int i = 0; i < Math.Min(count, guilds.Count * 8); i++)
                    settings.PendingUsers[i].Guild = new GuildData { guildId = (int)guilds[i % guilds.Count],
                        MemberType = i < guilds.Count ? 1 : 3, JoinedAt = now.Ticks, ActivityByDay = [] };
            }
            var previous = JsonDb.Instance.NpcManagement;
            JsonDb.Instance.NpcManagement = settings;
            try { JsonDb.Save(); } catch { JsonDb.Instance.NpcManagement = previous; throw; }
            Recover(db);
            return count;
        }
    }

    public static void Recover(GameContext db)
    {
        lock (JsonDb.SaveLock)
        {
            var pending = JsonDb.Instance.NpcManagement;
            if (pending == null || pending.PendingUsers.Count == 0) return;
            // The durable journal is written before SQL. SQL commits first; a crash is safely replayed by stable ID.
            using var transaction = db.Database.BeginTransaction();
            foreach (var user in pending.PendingUsers)
            {
                if (user.ManagedNpc == null || db.SdkUsers.Any(u => u.ID == user.ID))
                    throw new InvalidDataException("NPC creation journal conflicts with a login account.");
                if (JsonDb.GetUser(user.ID) is { } found && found.ManagedNpc?.Seed != user.ManagedNpc.Seed)
                    throw new InvalidDataException("NPC creation journal conflicts with an existing player.");
                var profile = db.Users.Find(user.ID);
                if (profile == null) db.Users.Add(new GameUser { ID = user.ID, Nickname = user.ManagedNpc.Name,
                    Description = user.Description, LastAction = user.ManagedNpc.LastActionUtc });
                else if (profile.Nickname != user.ManagedNpc.Name || profile.Description != user.Description)
                    throw new InvalidDataException("NPC creation journal conflicts with an existing profile.");
            }
            db.SaveChanges(); transaction.Commit();
            var users = JsonDb.Instance.Users;
            var guilds = JsonDb.Instance.Guilds;
            JsonDb.Instance.Users = [.. users, .. pending.PendingUsers.Where(n => users.All(u => u.ID != n.ID))];
            JsonDb.Instance.Guilds = new(guilds);
            foreach (var (id, guild) in pending.PendingGuilds)
            {
                if (guilds.TryGetValue(id, out var existing) && (!existing.ManagedNpcGuild || existing.Data.Name != guild.Data.Name))
                { JsonDb.Instance.Users = users; JsonDb.Instance.Guilds = guilds; throw new InvalidDataException("NPC union ID collision."); }
                JsonDb.Instance.Guilds[id] = guild;
            }
            var committed = Clone(pending);
            if (pending.PendingRequestId != null) committed.CreatedBatches[pending.PendingRequestId] = pending.PendingUsers.Count;
            committed.PendingRequestId = null; committed.PendingUsers.Clear(); committed.PendingGuilds.Clear();
            JsonDb.Instance.NpcManagement = committed;
            try { JsonDb.Save(); }
            catch { JsonDb.Instance.Users = users; JsonDb.Instance.Guilds = guilds; JsonDb.Instance.NpcManagement = pending; throw; }
        }
    }
}
