using EpinelPS.Data;
using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.Soloraid;

public sealed record SoloRaidSyncStatus(bool Enabled, string State, string Message, bool NeedsGameDataUpdate,
    int? RemoteSeasonNo, int? CurrentRaidId, DateTimeOffset? LastSuccessAtUtc, DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? StartAtUtc, DateTimeOffset? EndAtUtc);

/// <summary>An immutable, atomically persisted schedule catalog. It never reads or changes player saves.</summary>
public sealed class SoloRaidSeasonCatalog(Func<GameData> currentData, string cachePath)
{
    private sealed record Cache(int SchemaVersion, DateTimeOffset LastSuccessAtUtc, RemoteSoloRaidSeason[] Seasons);
    private Cache snapshot = new(1, DateTimeOffset.MinValue, []);
    private readonly object gate = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private DateTimeOffset? lastAttempt;
    private string? lastError;
    public static string DefaultCachePath => Path.Combine(AppContext.BaseDirectory, "cache", "solo-raid-nikkehub.json");

    public bool LoadCache()
    {
        lock (gate)
        {
            try
            {
                var info = new FileInfo(cachePath);
                if (!info.Exists) return false;
                if (info.Length > 128 * 1024) throw new InvalidDataException("Solo raid schedule cache is too large");
                var cache = JsonConvert.DeserializeObject<Cache>(File.ReadAllText(cachePath))
                    ?? throw new InvalidDataException("Empty solo raid cache");
                ValidateCache(cache);
                Volatile.Write(ref snapshot, cache);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { lastError = "读取赛季缓存失败"; return false; }
        }
    }

    public void Apply(RemoteSoloRaidSeason season, DateTimeOffset fetchedAt)
    {
        if (!season.IsValid) throw new InvalidDataException("Invalid remote solo season");
        lock (gate)
        {
            var old = Volatile.Read(ref snapshot);
            var entries = old.Seasons.Where(s => s.SeasonNo != season.SeasonNo).Append(season)
                .OrderByDescending(s => s.SeasonNo).Take(16).OrderBy(s => s.SeasonNo).ToArray();
            var next = new Cache(1, fetchedAt.ToUniversalTime(), entries);
            ValidateCache(next);
            string fullPath = Path.GetFullPath(cachePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(next, Formatting.Indented));
                File.Move(temporary, fullPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            // Publish only once persistence succeeds, so a restart cannot silently undo an activation.
            Volatile.Write(ref snapshot, next);
            lastAttempt = fetchedAt;
            lastError = null;
        }
    }

    private static void ValidateCache(Cache cache)
    {
        if (cache.SchemaVersion != 1 || cache.Seasons is null || cache.Seasons.Length is < 1 or > 16 ||
            cache.Seasons.Any(s => s is null || !s.IsValid) ||
            cache.Seasons.Select(s => s.SeasonNo).Distinct().Count() != cache.Seasons.Length)
            throw new InvalidDataException("Invalid solo raid cache schema");
        var sorted = cache.Seasons.OrderBy(s => s.SeasonNo).ToArray();
        for (int i = 1; i < sorted.Length; i++)
            if (sorted[i].StartAt < sorted[i - 1].EndAt)
                throw new InvalidDataException("Solo raid schedules overlap or go backwards");
    }

    public async Task<bool> RefreshAsync(HttpClient client, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            lock (gate) lastAttempt = now;
            var season = await NikkeHubSoloRaidSource.FetchAsync(client, cancellationToken);
            Apply(season, now);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or
                                      UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { lock (gate) lastError = "暂时无法同步 NikkeHub，继续使用已确认赛季。"; return false; }
        finally { refreshGate.Release(); }
    }

    public SoloRaidSeasonConfig? Resolve(SoloRaidSeasonConfig? fallback, DateTimeOffset now)
    {
        var data = currentData();
        var entries = Volatile.Read(ref snapshot).Seasons;
        var candidates = entries.Select(s => (Number: s.SeasonNo, Config: Map(data, s))).Where(s => s.Config is not null).ToList();
        if (fallback is { IsValid: true } && data.SoloRaidManagerTable.TryGetValue(fallback.RaidId, out var manager))
        {
            int number = manager.MonsterPreset - 10000;
            if (candidates.All(c => c.Number != number)) candidates.Add((number, fallback));
        }
        var started = candidates.Where(c => c.Config!.StartAt <= now).OrderByDescending(c => c.Number).FirstOrDefault();
        return started.Config ?? candidates.OrderBy(c => c.Config!.StartAt).FirstOrDefault().Config ?? fallback;
    }

    public SoloRaidSyncStatus GetStatus(SoloRaidSeasonConfig? fallback, DateTimeOffset now)
    {
        lock (gate)
        {
            var state = Volatile.Read(ref snapshot);
            var remote = state.Seasons.MaxBy(s => s.SeasonNo);
            var current = Resolve(fallback, now);
            bool missing = remote is not null && Map(currentData(), remote) is null;
            string message = missing
                ? $"已发现第{remote!.SeasonNo}期个人突袭，但本地缺少对应 Boss 数据，请先更新游戏数据。"
                : lastError ?? (remote is null ? "正在获取 NikkeHub 个人突袭赛季，暂用已确认配置。" :
                    $"已同步 NikkeHub 第{remote.SeasonNo}期个人突袭，将按固定赛季时间切换。" );
            return new(true, missing ? "update-game-data" : lastError is not null ? "cached" : remote is null ? "waiting" : "synced",
                message, missing, remote?.SeasonNo, current?.RaidId,
                state.LastSuccessAtUtc == DateTimeOffset.MinValue ? null : state.LastSuccessAtUtc, lastAttempt,
                current?.StartAt, current?.EndAt);
        }
    }

    public bool IsAvailable(int raidId)
    {
        var data = currentData();
        return data.SoloRaidManagerTable.TryGetValue(raidId, out var manager) &&
            HasCompleteStages(data, manager.MonsterPreset);
    }

    private static SoloRaidSeasonConfig? Map(GameData data, RemoteSoloRaidSeason remote)
    {
        var managers = data.SoloRaidManagerTable.Values.Where(m => m.MonsterPreset == 10000 + remote.SeasonNo).ToArray();
        if (managers.Length != 1 || !HasCompleteStages(data, managers[0].MonsterPreset)) return null;
        return new SoloRaidSeasonConfig { RaidId = managers[0].Id, VisibleAt = remote.StartAt, StartAt = remote.StartAt,
            EndAt = remote.EndAt, SettleAt = remote.EndAt, DisableAt = remote.EndAt.AddDays(7) };
    }

    private static bool HasCompleteStages(GameData data, int group)
    {
        for (int level = 1; level <= 8; level++)
        {
            var presets = data.SoloRaidPresetTable.Values.Where(p => p.PresetGroupId == group && p.WaveOrder == level).ToArray();
            if (presets.Length != 1 || !data.WaveIntercept001Table.TryGetValue(presets[0].Wave, out var wave) ||
                wave.TargetList is not { Count: > 0 }) return false;
            foreach (long id in wave.TargetList)
                if (!data.MonsterTable.TryGetValue(id, out var monster) || !data.MonsterStatEnhanceTable.Values.Any(s =>
                        s.GroupId == monster.StatenhanceId && s.Lv == presets[0].MonsterStageLv && s.LevelHp > 0)) return false;
        }
        return true;
    }
}
