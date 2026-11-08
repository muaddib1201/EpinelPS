using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.Utils;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    // New list/dictionary snapshots leave existing enumerators valid. Real-player objects are retained.
    private static void Change(Action action)
    {
        var core = JsonDb.Instance;
        var users = core.Users; var guilds = core.Guilds; var settings = core.NpcManagement;
        core.Users = users.Select(u => u.ManagedNpc == null ? u : Clone(u)).ToList();
        core.Guilds = guilds.ToDictionary(g => g.Key, g => Clone(g.Value));
        core.NpcManagement = Clone(settings ?? new());
        try { action(); JsonDb.Save(); }
        catch { core.Users = users; core.Guilds = guilds; core.NpcManagement = settings; throw; }
    }

    public static void Configure(bool enabled, double speed, DateTime now)
    {
        if (!double.IsFinite(speed) || speed is < .25 or > 4) throw new ArgumentException("速度倍率必须在 0.25～4 之间。");
        lock (JsonDb.SaveLock) Change(() =>
        {
            var settings = JsonDb.Instance.NpcManagement!;
            bool reschedule = settings.Enabled != enabled;
            settings.Enabled = enabled; settings.Speed = speed;
            if (reschedule)
                foreach (var user in JsonDb.Instance.Users.Where(u => u.ManagedNpc != null))
                    RescheduleDailyRoutine(user.ManagedNpc!, new[] { now, settings.LastProcessedUtc, user.ManagedNpc!.LastActionUtc }.Max());
        });
    }

    public static void Update(ulong id, bool enabled, int tier, int? guildId, DateTime now)
    {
        if (tier is < 0 or > 2) throw new ArgumentException("未知成长档次。");
        lock (JsonDb.SaveLock)
        {
            var user = JsonDb.GetUser(id);
            if (user?.ManagedNpc == null) throw new ArgumentException("找不到这个托管 NPC。");
            if (guildId != user.Guild.guildId)
            {
                if (user.Guild.MemberType == 1) throw new ArgumentException("会长暂不支持调离联盟。");
                if (guildId.HasValue)
                {
                    if (!JsonDb.Instance.Guilds.TryGetValue(guildId.Value, out var guild)) throw new ArgumentException("联盟不存在。");
                    int cap = GameData.Instance.GuildLevels[guild.Data.Grade].MaxMemberCount;
                    if (JsonDb.Instance.Users.Count(u => u.Guild.guildId == guildId) >= cap) throw new ArgumentException("联盟人数已满。");
                }
            }
            Change(() =>
            {
                var npc = JsonDb.GetUser(id)!;
                var state = npc.ManagedNpc!;
                state.Enabled = enabled; state.Tier = ActiveTier;
                var settings = JsonDb.Instance.NpcManagement!;
                if (state.Enabled != user.ManagedNpc!.Enabled)
                    RescheduleDailyRoutine(state, new[] { now, settings.LastProcessedUtc, state.LastActionUtc }.Max());
                if (npc.Guild.guildId != guildId)
                {
                    if (npc.Guild.guildId is int oldId && JsonDb.Instance.Guilds.TryGetValue(oldId, out var oldGuild))
                        GuildJournal.Left(oldGuild, npc, state.Name, now);
                    npc.Guild = new GuildData { guildId = guildId, MemberType = guildId.HasValue ? 3 : 0,
                        JoinedAt = guildId.HasValue ? now.Ticks : 0, LeaveAt = guildId.HasValue ? null : now.Ticks, ActivityByDay = [] };
                    if (guildId is int newId) GuildJournal.Joined(JsonDb.Instance.Guilds[newId], npc, state.Name, now);
                }
            });
        }
    }

    private static void Shoot(User user, SavedGuild guild, DateTime at, Random rng)
    {
        var stages = GameData.Instance.ShootingRanges.Values.Where(s => !s.SpecActiveSpot).OrderBy(s => s.Id).ToArray();
        if (stages.Length == 0) return;
        var stage = stages[rng.Next(stages.Length)];
        long combat = user.RepresentationTeamDataNew.Sum(csn => (long)FormulaUtils.CalculateCP(user, csn));
        int seconds = stage.ShootingRangeBattleLengthType == ShootingRangeBattleLength.Short ? 90 : 180;
        long score = Math.Max(1, (long)(combat * seconds * (.5 + rng.NextDouble())));
        if (!guild.ShootingRangeScores.TryGetValue(stage.Id, out var scores)) guild.ShootingRangeScores[stage.Id] = scores = [];
        scores[user.ID] = Math.Max(scores.GetValueOrDefault(user.ID), score);
        var log = new NetShootingRangeBattleLog { ShootingRangeId = stage.Id, Score = score, BattleResult = 1,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(at, DateTimeKind.Utc)), UserTeam = new NetTeamData { TeamNumber = 1 } };
        for (int i = 0; i < user.RepresentationTeamDataNew.Length; i++)
        {
            var c = user.GetCharacterBySerialNumber(user.RepresentationTeamDataNew[i])!;
            log.UserTeam.Slots.Add(new NetTeamSlot { Slot = i + 1, Value = c.Csn });
            log.CharacterDataList.Add(new NetShootingRangeBattleLogCharacterData { Tid = c.Tid, NameCode = GameData.Instance.CharacterTable[c.Tid].NameCode,
                Slot = i + 1, CostumeId = c.CostumeId, Lv = user.GetCharacterLevel(c.Csn, c.Level), Grade = Math.Min(3, c.Grade), Core = Math.Max(0, c.Grade - 3),
                Combat = FormulaUtils.CalculateCP(user, c.Csn) });
        }
        user.ShootingRangeBattles.Insert(0, new SavedShootingRangeBattle { GuildId = guild.Data.Gsn, BattleLog = log.ToByteArray() });
        user.ShootingRangeBattles = user.ShootingRangeBattles.Take(100).ToList();
        Log(user.ManagedNpc!, at, $"射击场练习：{score:N0} 分");
    }

    private static void Log(ManagedNpcState n, DateTime at, string text)
    {
        n.Actions.Insert(0, new NpcActionRecord { AtUtc = at, Text = text });
        if (n.Actions.Count > 50) n.Actions.RemoveRange(50, n.Actions.Count - 50);
    }
}
