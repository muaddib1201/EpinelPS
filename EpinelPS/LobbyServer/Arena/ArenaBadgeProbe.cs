using EpinelPS.Utils;
using Google.Protobuf.WellKnownTypes;
using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.Arena;

// Diagnostic switches for locating what lights the Ark arena "N" badge. The client logic
// is not readable, so each reply input can be varied one at a time while the player opens
// the Ark. Without arena-badge-probe.json next to the server every reply is unchanged.
// Only reply fields are changed; saved data is never touched.
internal static class ArenaBadgeProbe
{
    internal sealed class Settings
    {
        // default | omit | now | epoch
        public string EnterTime { get; set; } = "default";
        // default | false | true
        public string RankingNewReward { get; set; } = "default";
        // default | used (report every free attempt as spent)
        public string SpecialFree { get; set; } = "default";
        public string RookieFree { get; set; } = "default";
        // default | zero
        public string SpecialReward { get; set; } = "default";
        // default | omit | alltime
        public string Schedule { get; set; } = "default";
        // default | omit | seasonclosed | success
        public string Champion { get; set; } = "default";
    }

    private static readonly object Gate = new();
    private static (DateTime Written, long Length) loadedStamp;
    private static Settings current = new();

    internal static string FilePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "arena-badge-probe.json");

    internal static Settings Current
    {
        get
        {
            lock (Gate)
            {
                var file = new FileInfo(FilePath);
                var stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : (DateTime.MinValue, -1L);
                if (stamp == loadedStamp) return current;
                loadedStamp = stamp;
                try
                {
                    current = file.Exists ? Validate(JsonConvert.DeserializeObject<Settings>(File.ReadAllText(file.FullName)) ?? new()) : new();
                }
                catch (Exception ex)
                {
                    // An unreadable or invalid file must not affect players; fall back to normal replies.
                    current = new();
                    Logging.WriteLine($"[ArenaBadgeProbe] ignored invalid {file.Name}: {ex.Message}", LogType.Warning);
                }
                Logging.WriteLine($"[ArenaBadgeProbe] active settings: {JsonConvert.SerializeObject(current)}", LogType.Warning);
                return current;
            }
        }
    }

    private static Settings Validate(Settings s)
    {
        static void Allow(string name, string value, params string[] allowed)
        {
            if (!allowed.Contains(value)) throw new InvalidDataException($"{name} must be one of: {string.Join(", ", allowed)}");
        }
        Allow(nameof(s.EnterTime), s.EnterTime, "default", "omit", "now", "epoch");
        Allow(nameof(s.RankingNewReward), s.RankingNewReward, "default", "false", "true");
        Allow(nameof(s.SpecialFree), s.SpecialFree, "default", "used");
        Allow(nameof(s.RookieFree), s.RookieFree, "default", "used");
        Allow(nameof(s.SpecialReward), s.SpecialReward, "default", "zero");
        Allow(nameof(s.Schedule), s.Schedule, "default", "omit", "alltime");
        Allow(nameof(s.Champion), s.Champion, "default", "omit", "seasonclosed", "success");
        return s;
    }

    private static void Note(string what) => Logging.WriteLine($"[ArenaBadgeProbe] {what}", LogType.Warning);

    internal static void ApplyEnterTime(ResPermanentContentBadgeData response, DateTime utc)
    {
        string mode = Current.EnterTime;
        if (mode == "default") return;
        var arena = response.ContentEnterTimes.Where(t => t.ContentType == NetEnteredContentType.Arena).ToArray();
        foreach (var entry in arena) response.ContentEnterTimes.Remove(entry);
        if (mode != "omit")
            response.ContentEnterTimes.Add(new NetContentEnterTimeData { ContentType = NetEnteredContentType.Arena,
                EnteredAt = Timestamp.FromDateTime(mode == "now" ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : DateTime.UnixEpoch) });
        Note($"permanentcontent arena entered time: {mode}");
    }

    internal static bool ApplyRankingNewReward(bool actual)
    {
        string mode = Current.RankingNewReward;
        if (mode == "default") return actual;
        Note($"ranking hasNewReward: {mode} (actual {actual})");
        return mode == "true";
    }

    internal static int ApplyFreeCount(bool special, int used, int free)
    {
        if ((special ? Current.SpecialFree : Current.RookieFree) != "used") return used;
        Note($"{(special ? "special" : "rookie")} arena count reported as {Math.Max(used, free)} (actual {used})");
        return Math.Max(used, free);
    }

    // Login user data carries the same daily attempt counts as the arena replies.
    internal static void ApplyUserData(NetUserData data)
    {
        data.ArenaCount = ApplyFreeCount(false, data.ArenaCount, 5);
        if (Current.SpecialFree != "used") return;
        int specialFree = Data.GameData.Instance.Arenas.Values.FirstOrDefault(r => r.ArenaType == Data.ArenaType.ArenaSpecial)?.FreePlayCount ?? 2;
        data.SpecialArenaCount = ApplyFreeCount(true, data.SpecialArenaCount, specialFree);
    }

    // Returns null when the badge data should be left out of the reply.
    internal static NetChampionArenaBadgeData? ApplyChampion(NetChampionArenaBadgeData badge)
    {
        string mode = Current.Champion;
        if (mode == "default") return badge;
        Note($"champion arena badge data: {mode}");
        if (mode == "omit") return null;
        badge.ChampionArenaContentsState = mode == "success" ? ChampionArenaContentsState.Success : ChampionArenaContentsState.SeasonClosed;
        return badge;
    }

    internal static void ApplyChampionEntry(ResGetChampionArena entry)
    {
        string mode = Current.Champion;
        if (mode == "default") return;
        entry.BadgeData = ApplyChampion(entry.BadgeData);
        if (mode != "omit") entry.ChampionArenaContentsState = entry.BadgeData!.ChampionArenaContentsState;
    }

    internal static long ApplySpecialReward(long amount)
    {
        if (Current.SpecialReward != "zero") return amount;
        Note($"special reward box reported as 0 (actual {amount})");
        return 0;
    }

    internal static void ApplySchedule(Google.Protobuf.Collections.RepeatedField<NetSchedule> schedules)
    {
        string mode = Current.Schedule;
        if (mode == "default") return;
        schedules.Clear();
        if (mode == "alltime") schedules.Add(new NetSchedule { AllTime = new() });
        Note($"arena schedules: {mode}");
    }
}
