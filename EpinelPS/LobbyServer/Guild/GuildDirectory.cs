using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;
using Google.Protobuf;

namespace EpinelPS.LobbyServer.Guild;

internal static class GuildDirectory
{
    internal const int Master = 1;
    private const int Open = 0;

    internal static int RejoinCooldownSeconds()
    {
        foreach (var key in new[] { "GuildJoinWaitingTime", "GuildRejoinLimitTime", "GuildRejoinTime", "GuildJoinLimitTime", "GuildReJoinTime" })
            if (GameData.Instance.GameSettings.TryGetValue(key, out var row) && int.TryParse(row.Value, out int seconds) && seconds >= 0)
                return seconds;
        return 86400;
    }

    internal static SavedGuild? Find(long id) => id is > 0 and <= int.MaxValue
        ? JsonDb.Instance.Guilds.GetValueOrDefault((int)id) : null;

    internal static NetSimpleGuildData? Membership(User user)
    {
        lock (JsonDb.SaveLock)
        {
            var guild = Find(user.Guild.guildId ?? 0);
            return guild == null ? null : Summary(guild);
        }
    }

    internal static NetSimpleGuildData Summary(SavedGuild guild)
    {
        GuildActivity.Refresh(guild, DateTime.UtcNow);
        var data = guild.Data;
        return new NetSimpleGuildData
        {
            Gsn = data.Gsn, Name = data.Name, Emblem = data.Emblem, Grade = data.Grade,
            ActivityPoint = data.ActivityPoint, ActivityPointDaily = data.ActivityPointDaily,
            ActivityPointRecent = data.ActivityPointRecent, IsBot = data.IsBot,
            UnionRaidTier = data.UnionRaidTier, UnionRaidTierNumber = data.UnionRaidTierNumber,
            MemberCount = JsonDb.Instance.Users.Count(u => u.Guild.guildId == data.Gsn)
        };
    }

    internal static IEnumerable<NetGuildMemberData> Members(GameContext db, SavedGuild guild)
    {
        var members = JsonDb.Instance.Users.Where(u => u.Guild.guildId == guild.Data.Gsn).ToArray();
        var now = DateTime.UtcNow;
        foreach (var user in members) GuildActivity.EnsureMember(user, now);
        GuildActivity.Refresh(guild, now);
        return members.Select(u => Member(db, u, u.Guild));
    }

    private static NetGuildMemberData Member(GameContext db, User user, GuildData membership) => new()
    {
        Usn = (long)user.ID, Nickname = db.Users.Find(user.ID)?.Nickname ?? "Player",
        Type = membership.MemberType, JoinedAt = membership.JoinedAt, Lv = user.userPointData.UserLevel,
        Server = 1001, LastActionAt = Math.Max(user.ManagedNpc?.LastActionUtc.Ticks ?? user.LastLogin.Ticks, membership.JoinedAt),
        ActivityPointDaily = GuildActivity.Daily(user, DateTime.UtcNow),
        ActivityPointRecent = GuildActivity.Recent(user, DateTime.UtcNow),
        Icon = user.ProfileIconId, IconPrism = user.ProfileIconIsPrism, Frame = user.ProfileFrame,
        TeamCombat = NetUtils.GetDisplayedTeam(user).TeamCombat, UserTitleId = user.TitleId,
        SendMailAt = new(), UserTitleDisplayData = new()
    };

    internal static ResCreateGuild Create(GameContext db, User user, ReqCreateGuild req, byte[]? nonce)
    {
        // A retransmission of the same authenticated packet needs the original successful reply.
        string? requestKey = nonce is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData([..nonce, ..req.ToByteArray()])) : null;
        if (Find(user.Guild.guildId ?? 0) is { } existing)
        {
            if (user.Guild.MemberType == Master && requestKey != null && existing.CreationRequestKey == requestKey &&
                existing.CreationReceipt != null)
            {
                var receipt = ResCreateGuild.Parser.ParseFrom(existing.CreationReceipt);
                // Older receipts omit these member messages. Preserve the original balances on replay.
                foreach (var member in receipt.Members)
                {
                    member.SendMailAt ??= new();
                    member.UserTitleDisplayData ??= new();
                }
                return receipt;
            }
            return new() { Result = CreateGuildResult.AlreadyJoin };
        }

        var name = req.Name.Normalize();
        if (!IsValidName(name)) return new() { Result = CreateGuildResult.Inappropriate };
        var registry = JsonDb.Instance.Guilds;
        if (registry.Values.Concat(JsonDb.Instance.NpcManagement?.PendingGuilds.Values ?? Enumerable.Empty<SavedGuild>())
            .Any(g => string.Equals(g.Data.Name, name, StringComparison.OrdinalIgnoreCase)))
            return new() { Result = CreateGuildResult.ExistName };

        var data = GameData.Instance;
        var unlock = data.ContentsOpenTable[ContentsOpen.Guild].OpenCondition.Single(c => c.OpenConditionType == ContentsOpenCondition.UserLevel);
        if (user.userPointData.UserLevel < unlock.OpenConditionValue)
            throw new BadHttpRequestException("The commander has not unlocked Unions.", 400);
        int emblem = req.Emblem == 0 ? Setting("DefaultGuildEmblem") : req.Emblem;
        if (!data.GuildEmblems.TryGetValue(emblem, out var emblemRow) || emblemRow.Grade > 1)
            throw new BadHttpRequestException("This emblem is unavailable to a new Union.", 400);

        // Reserve IDs still referenced by older saves as well as existing guilds.
        int id = 1;
        while (registry.ContainsKey(id) || JsonDb.Instance.NpcManagement?.PendingGuilds.ContainsKey(id) == true ||
            JsonDb.Instance.Users.Any(u => u.Guild.guildId == id)) id = checked(id + 1);
        var membership = new GuildData { guildId = id, MemberType = Master, JoinedAt = DateTime.UtcNow.Ticks, ActivityByDay = [] };
        var founder = Member(db, user, membership);
        var guild = new SavedGuild
        {
            Data = new NetGuildData { Gsn = id, Name = name, Emblem = emblem, Locale = req.Locale,
                Grade = 1, JoinType = Open, JoinLevel = Setting("DefaultGuildJoinLevel") },
            CreationLog = new NetGuildLog { Seq = 1, Id = "GUILD_JOURNAL_GUILD_CREATE", CreatedAt = membership.JoinedAt,
                Arguments = { new NetGuildLogArgs { Value = founder.Nickname } } }
        };
        var response = new ResCreateGuild { Guild = guild.Data, Members = { founder } };
        var fee = CreationFee(user);
        foreach (var (type, amount) in fee)
            response.Currencies.Add(new NetUserCurrencyData { Type = (int)type, Value = user.Currency[type] - amount });
        if (requestKey != null)
        {
            guild.CreationRequestKey = requestKey;
            guild.CreationReceipt = response.ToByteArray();
        }
        var previous = user.Guild;
        registry.Add(id, guild);
        user.Guild = membership;
        try
        {
            foreach (var balance in response.Currencies) user.Currency[(CurrencyType)balance.Type] = balance.Value;
            JsonDb.Save();
        }
        catch
        {
            foreach (var balance in response.Currencies)
            {
                var type = (CurrencyType)balance.Type;
                user.Currency[type] = balance.Value + fee[type];
            }
            user.Guild = previous;
            registry.Remove(id);
            throw;
        }
        return response;
    }

    private static int Setting(string key) =>
        int.Parse(GameData.Instance.GameSettings[key].Value!, CultureInfo.InvariantCulture);

    private static Dictionary<CurrencyType, long> CreationFee(User user)
    {
        var type = (CurrencyType)Setting("GuildCreationCurrencyType");
        long cost = Setting("GuildCreationCurrencyValue");
        if (cost < 0) throw new InvalidDataException("Invalid Union creation cost.");
        var fee = new Dictionary<CurrencyType, long>();
        long balance = user.Currency.GetValueOrDefault(type);
        long first = Math.Min(Math.Max(0, balance), cost);
        if (first > 0) fee.Add(type, first);
        long remaining = cost - first;
        if (remaining == 0) return fee;
        if (type != CurrencyType.FreeCash || user.Currency.GetValueOrDefault(CurrencyType.ChargeCash) < remaining)
            throw new BadHttpRequestException("Insufficient currency to create a Union.", 400);
        fee.Add(CurrencyType.ChargeCash, remaining);
        return fee;
    }

    private static bool IsValidName(string name)
    {
        var letters = name.EnumerateRunes().ToArray();
        return letters.Length is >= 2 and <= 8 && letters.Any(Rune.IsLetter) &&
            letters.All(r => Rune.IsLetterOrDigit(r) && r.Value is not (>= 0x1100 and <= 0x11ff
                or >= 0x3130 and <= 0x318f or >= 0xa960 and <= 0xa97f or >= 0xd7b0 and <= 0xd7ff
                or >= 0xffa0 and <= 0xffdc));
    }
}
