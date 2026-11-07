using EpinelPS.Database;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    public const int ActiveTier = 2;
    public const int DailyTrainingLimit = 6;
    public const int DailyStageLimit = 12;
    public const int DailyGuildContribution = 30;
    internal static readonly string[] DefaultGuildNames = ["星屿茶会", "晨曦前线", "长夜灯塔", "OverClock", "貓咪補給站"];

    public static int EnsureNpcFriends(ulong ownerId, DateTime now)
    {
        lock (JsonDb.SaveLock)
        {
            var user = JsonDb.GetUser(ownerId) ?? throw new ArgumentException("好友所属账号不存在。");
            if (user.ManagedNpc != null) throw new ArgumentException("请选择真实玩家账号。");
            if (user.Friendships?.NpcFriendsInitialized == true) return 0;
            int added = 0;
            // Managed snapshots intentionally retain human object identity.
            // Seed also changes this owner, so restore that half on failed commit.
            var previous = user.Friendships == null ? null : Clone(user.Friendships);
            try { Change(() => added = LobbyServer.Friend.FriendshipService.Seed(JsonDb.GetUser(ownerId)!)); }
            catch { user.Friendships = previous; throw; }
            return added;
        }
    }

    public static void UpgradeSocial(DateTime now)
    {
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.Instance.NpcManagement?.SocialSchemaVersion >= 1 || !JsonDb.Instance.Users.Any(u => u.ManagedNpc != null)) return;
            Change(() =>
            {
                foreach (var user in JsonDb.Instance.Users.Where(u => u.ManagedNpc != null))
                    user.ManagedNpc!.Tier = ActiveTier;
                AddMissingGuilds(now);
                JsonDb.Instance.NpcManagement!.SocialSchemaVersion = 1;
            });
        }
    }

    private static void AddMissingGuilds(DateTime now)
    {
        var core = JsonDb.Instance;
        int missing = Math.Max(0, DefaultGuildNames.Length - core.Guilds.Values.Count(g => g.ManagedNpcGuild));
        var free = new Queue<User>(core.Users.Where(u => u.ManagedNpc != null && u.Guild.guildId == null).OrderBy(u => u.ID));
        // Preserve existing membership even when a small custom population cannot found all five unions.
        missing = Math.Min(missing, free.Count);
        for (int i = 0; i < missing; i++)
        {
            int id = 1;
            while (core.Guilds.ContainsKey(id) || core.Users.Any(u => u.Guild.guildId == id) || core.NpcManagement!.PendingGuilds.ContainsKey(id)) id++;
            string name = DefaultGuildNames.FirstOrDefault(n => core.Guilds.Values.All(g => g.Data.Name != n)) ?? $"前線支部{id}";
            var founder = free.Dequeue();
            var guild = new SavedGuild
            {
                ManagedNpcGuild = true,
                Data = new NetGuildData { Gsn = id, Name = name, Desc = name == "OverClock" ? "Keep growing, keep fighting." : "補給到位，一起養成、一起出擊！",
                    Emblem = 1, Locale = "zh-TW", Grade = 1, JoinType = 0, JoinLevel = 10 },
                CreationLog = new NetGuildLog { Seq = 1, Id = "GUILD_JOURNAL_GUILD_CREATE", CreatedAt = now.Ticks,
                    Arguments = { new NetGuildLogArgs { Value = founder.ManagedNpc!.Name } } }
            };
            core.Guilds.Add(id, guild);
            founder.Guild = new GuildData { guildId = id, MemberType = 1, JoinedAt = now.Ticks, ActivityByDay = [] };
            int count = Math.Min(7, free.Count - (missing - i - 1));
            for (int member = 0; member < count; member++)
                free.Dequeue().Guild = new GuildData { guildId = id, MemberType = 3, JoinedAt = now.Ticks, ActivityByDay = [] };
        }
    }
}
