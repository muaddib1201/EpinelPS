using EpinelPS.Data;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Soloraid;

public static class SoloRaidSeasonSource
{
    private static SoloRaidSeasonCatalog? catalog;

    public static void Bind(SoloRaidSeasonCatalog? value) => Volatile.Write(ref catalog, value);

    public static SoloRaidSeasonConfig? Current(DateTimeOffset? utcNow = null)
    {
        var config = GameConfig.Root;
        var active = Volatile.Read(ref catalog);
        return config.SoloRaidAutoSync && active is not null
            ? active.Resolve(config.SoloRaidSeason, utcNow ?? DateTimeOffset.UtcNow) : config.SoloRaidSeason;
    }

    public static bool IsAvailable(int raidId)
    {
        var config = GameConfig.Root;
        var active = Volatile.Read(ref catalog);
        return config.SoloRaidAutoSync && active is not null
            ? active.IsAvailable(raidId) : GameData.Instance.SoloRaidManagerTable.ContainsKey(raidId);
    }

    public static SoloRaidSyncStatus GetStatus()
    {
        var config = GameConfig.Root;
        var active = Volatile.Read(ref catalog);
        if (config.SoloRaidAutoSync && active is not null)
            return active.GetStatus(config.SoloRaidSeason, DateTimeOffset.UtcNow);
        return new(config.SoloRaidAutoSync, config.SoloRaidAutoSync ? "waiting" : "disabled",
            config.SoloRaidAutoSync ? "等待获取 NikkeHub 个人突袭赛季。" : "个人突袭自动同步已关闭，使用固定配置。",
            false, null, config.SoloRaidSeason?.RaidId, null, null, config.SoloRaidSeason?.StartAt, config.SoloRaidSeason?.EndAt);
    }
}
