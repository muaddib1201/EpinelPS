using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Guild;

internal static class GuildJournal
{
    // Verified in the installed Locale_System table; arguments are literal nicknames.
    private const string JoinKey = "GUILD_JOURNAL_JOIN";
    private const string QuitKey = "GUILD_JOURNAL_QUIT";

    internal static IReadOnlyList<NetGuildLog> Read(GameContext db, SavedGuild guild)
    {
        var previous = guild.Journal;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var member in JsonDb.Instance.Users.Where(u => u.Guild.guildId == guild.Data.Gsn)
                         .OrderBy(u => u.Guild.JoinedAt).ThenBy(u => u.ID))
                Joined(guild, member, Nickname(db, member), now);
            if (!ReferenceEquals(previous, guild.Journal)) JsonDb.Save();
        }
        catch { guild.Journal = previous; throw; }
        return guild.Journal.Select(e => e.Log).Append(guild.CreationLog)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Seq).ToArray();
    }

    internal static string Nickname(GameContext db, User user) =>
        db.Users.Find(user.ID)?.Nickname ?? user.ManagedNpc?.Name ?? user.ID.ToString();

    internal static void Joined(SavedGuild guild, User member, string nickname, DateTime now)
    {
        long joined = member.Guild.JoinedAt;
        if (joined <= 0 || joined < guild.CreationLog.CreatedAt || joined > now.Ticks ||
            (member.Guild.MemberType == GuildDirectory.Master && joined == guild.CreationLog.CreatedAt) ||
            guild.Journal.Any(e => e.MemberId == member.ID && e.JoinedAt == joined && e.Log.Id == JoinKey)) return;
        Append(guild, member.ID, joined, JoinKey, nickname, joined);
    }

    internal static void Left(SavedGuild guild, User member, string nickname, DateTime at)
    {
        Joined(guild, member, nickname, at);
        Append(guild, member.ID, member.Guild.JoinedAt, QuitKey, nickname, at.Ticks);
    }

    private static void Append(SavedGuild guild, ulong id, long joined, string key, string nickname, long at)
    {
        long sequence = checked(guild.Journal.Select(e => e.Log.Seq).Append(guild.CreationLog.Seq).Max() + 1);
        var entry = new SavedGuildJournalEntry { MemberId = id, JoinedAt = joined,
            Log = new NetGuildLog { Seq = sequence, Id = key, CreatedAt = at,
                Arguments = { new NetGuildLogArgs { Value = nickname, IsLocaleKey = false } } } };
        // Replacing the list lets membership transactions restore their previous journal on failure.
        guild.Journal = [.. guild.Journal, entry];
    }
}
