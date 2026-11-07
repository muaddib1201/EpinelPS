using EpinelPS.Data;
using EpinelPS.Database;
using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.Guild;

internal static class GuildActivity
{
    internal static DateTime DayStart(DateTime utc) =>
        utc.AddHours(-JsonDb.Instance.ResetHourUtcTime).Date.AddHours(JsonDb.Instance.ResetHourUtcTime);
    internal static int Day(DateTime utc) => int.Parse(DayStart(utc).ToString("yyyyMMdd"));

    private static bool Prepare(SavedGuild guild)
    {
        if (guild.ActivityByDay != null) return false;
        guild.ActivityByDay = [];
        // Old NPC saves have one dated daily total; older totals have no reliable dates.
        if (guild.NpcActivityDay > 0 && guild.Data.ActivityPointDaily > 0)
            guild.ActivityByDay[guild.NpcActivityDay] = guild.Data.ActivityPointDaily;
        return true;
    }

    internal static bool EnsureMember(User user, DateTime now)
    {
        if (GuildDirectory.Find(user.Guild.guildId ?? 0) is not { } guild) return false;
        bool changed = Prepare(guild);
        if (user.Guild.ActivityByDay != null)
        {
            foreach (var (day, amount) in user.Guild.ActivityByDay)
                if (user.GuildContributionByDay.GetValueOrDefault(day) < amount)
                {
                    user.GuildContributionByDay[day] = amount;
                    changed = true;
                }
            return changed;
        }
        user.Guild.ActivityByDay = [];
        if (user.ManagedNpc is { } npc)
        {
            // These points were already included in the legacy Union total.
            if (npc.Day > 0 && npc.ActivityToday > 0)
            {
                user.Guild.ActivityByDay[npc.Day] = Math.Min(100, npc.ActivityToday);
                user.GuildContributionByDay[npc.Day] = Math.Max(user.GuildContributionByDay.GetValueOrDefault(npc.Day), Math.Min(100, npc.ActivityToday));
            }
        }
        else if (user.LastReset >= DateTime.UnixEpoch && user.LastReset <= now &&
                 user.Guild.JoinedAt > 0 && user.Guild.JoinedAt <= DayStart(user.LastReset).Ticks)
        {
            // Only recover claims from a day for which membership is provable.
            Credit(user, guild, user.ResetableData.DailyMissionPoints, user.LastReset);
        }
        return true;
    }

    internal static int Record(User user, int points, DateTime at)
    {
        if (GuildDirectory.Find(user.Guild.guildId ?? 0) is not { } guild || points <= 0) return 0;
        EnsureMember(user, at);
        int credited = Credit(user, guild, points, at);
        Refresh(guild, at);
        return credited;
    }

    private static int Credit(User user, SavedGuild guild, int points, DateTime at)
    {
        int day = Day(at);
        int previous = user.Guild.ActivityByDay!.GetValueOrDefault(day);
        int spent = user.GuildContributionByDay.GetValueOrDefault(day);
        int credit = Math.Min(Math.Max(0, points), Math.Max(0, 100 - Math.Max(previous, spent)));
        if (credit == 0) return 0;
        user.Guild.ActivityByDay[day] = previous + credit;
        user.GuildContributionByDay[day] = spent + credit;
        guild.ActivityByDay![day] = guild.ActivityByDay.GetValueOrDefault(day) + credit;
        guild.Data.ActivityPoint += credit;
        guild.Data.Exp += credit;
        while (GameData.Instance.GuildLevels.TryGetValue(guild.Data.Grade, out var level) && level.Exp > 0 &&
               GameData.Instance.GuildLevels.ContainsKey(guild.Data.Grade + 1) && guild.Data.Exp >= level.Exp)
        {
            guild.Data.Exp -= level.Exp;
            guild.Data.Grade++;
        }
        return credit;
    }

    internal static void Refresh(SavedGuild guild, DateTime now)
    {
        Prepare(guild);
        int day = Day(now), first = Day(now.AddDays(-6));
        guild.Data.ActivityPointDaily = checked((int)guild.ActivityByDay!.GetValueOrDefault(day));
        guild.Data.ActivityPointRecent = checked((int)guild.ActivityByDay.Where(p => p.Key >= first && p.Key <= day).Sum(p => p.Value));
    }

    internal static int Daily(User user, DateTime now) => user.Guild.ActivityByDay?.GetValueOrDefault(Day(now)) ?? 0;
    internal static int Recent(User user, DateTime now)
    {
        int day = Day(now), first = Day(now.AddDays(-6));
        return user.Guild.ActivityByDay?.Where(p => p.Key >= first && p.Key <= day).Sum(p => p.Value) ?? 0;
    }

    internal static void RecoverAll(DateTime now)
    {
        lock (JsonDb.SaveLock)
        {
            var guilds = JsonConvert.DeserializeObject<Dictionary<int, SavedGuild>>(JsonConvert.SerializeObject(JsonDb.Instance.Guilds))!;
            var memberships = JsonDb.Instance.Users.ToDictionary(u => u.ID, u => JsonConvert.DeserializeObject<GuildData>(JsonConvert.SerializeObject(u.Guild))!);
            var budgets = JsonDb.Instance.Users.ToDictionary(u => u.ID, u => new Dictionary<int, int>(u.GuildContributionByDay));
            try
            {
                foreach (var user in JsonDb.Instance.Users) EnsureMember(user, now);
                foreach (var guild in JsonDb.Instance.Guilds.Values) Refresh(guild, now);
                JsonDb.Save();
            }
            catch
            {
                JsonDb.Instance.Guilds = guilds;
                foreach (var user in JsonDb.Instance.Users)
                {
                    user.Guild = memberships[user.ID];
                    user.GuildContributionByDay = budgets[user.ID];
                }
                throw;
            }
        }
    }
}
