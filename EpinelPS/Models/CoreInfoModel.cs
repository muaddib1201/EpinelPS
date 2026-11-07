using EpinelPS.Utils;

namespace EpinelPS.Models;

/// <summary>
/// NOTE: OBSOLETE: Add new entries to GameContext
/// </summary>
public class CoreInfo
{
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public ArenaWorld? Arena { get; set; }
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public NpcManagementSettings? NpcManagement { get; set; }
    // TODO: Move to GameContext
    public List<User> Users { get; set; } = [];
    // The registry, founder membership and wallet are committed in the same save.
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public Dictionary<int, SavedGuild> Guilds { get; set; } = [];
    public bool ShouldSerializeGuilds() => Guilds.Count != 0;
    public RankData RankDatas { get; set; } = new();

    // General configuration will stay here
    public byte[] LauncherTokenKey { get; set; } = [];
    public int DbVersion { get; set; } = 5;
    public bool BondRewardLevelsMigrated { get; set; }
    public byte[] EncryptionTokenKey { get; set; } = [];
    public LogType LogLevel { get; set; } = LogType.Debug;

    public int MaxInterceptionCount { get; set; } = 3;
    public int ResetHourUtcTime { get; set; } = 20;

    /// <summary>
    /// Globally active event banner IDs. If empty, falls back to user-specific banners or latest event.
    /// </summary>
    public List<int> ActiveEventBannerIds { get; set; } = [];

    // MARIA-n's announced opening, 2026-10-29 at 07:00 UTC+9. Private reruns can set their own start.
    public Dictionary<int, DateTime> ChallengeEventStartUtc { get; set; } = new()
    {
        [60124] = new DateTime(2026, 10, 28, 22, 0, 0, DateTimeKind.Utc)
    };
}
