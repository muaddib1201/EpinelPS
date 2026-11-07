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
            bool reschedule = settings.Enabled != enabled || settings.Speed != speed;
            settings.Enabled = enabled; settings.Speed = speed;
            if (reschedule)
                foreach (var user in JsonDb.Instance.Users.Where(u => u.ManagedNpc != null))
                    user.ManagedNpc!.NextActionUtc = Next(user.ManagedNpc,
                        new[] { now, settings.LastProcessedUtc, user.ManagedNpc.LastActionUtc }.Max(), speed);
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
                state.NextActionUtc = Next(state, new[] { now, settings.LastProcessedUtc, state.LastActionUtc }.Max(), settings.Speed);
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

    public static void Tick(DateTime now)
    {
        lock (JsonDb.SaveLock)
        {
            var settings = JsonDb.Instance.NpcManagement;
            if (settings?.Enabled != true || now < settings.LastProcessedUtc ||
                !JsonDb.Instance.Users.Any(u => u.ManagedNpc is { Enabled: true } n && n.NextActionUtc <= now)) return;
            Change(() =>
            {
                var queue = new PriorityQueue<User, (DateTime At, ulong Id)>();
                foreach (var user in JsonDb.Instance.Users.Where(u => u.ManagedNpc is { Enabled: true }))
                {
                    var n = user.ManagedNpc!;
                    // At most three days of offline work; skipped time cannot reappear on the next tick.
                    if (n.NextActionUtc < now.AddHours(-72)) n.NextActionUtc = now.AddHours(-72);
                    if (n.NextActionUtc <= now) queue.Enqueue(user, (n.NextActionUtc, user.ID));
                }
                // Merge all timelines so shared guild day boundaries advance once, in chronological order.
                while (queue.TryDequeue(out var user, out _))
                {
                    var n = user.ManagedNpc!;
                    var at = n.NextActionUtc;
                    int activeHour = (at.Hour - n.ActiveStartHourUtc + 24) % 24;
                    if (activeHour >= n.ActiveHours)
                    { n.NextActionUtc = at.Date.AddHours(n.ActiveStartHourUtc); if (n.NextActionUtc <= at) n.NextActionUtc = n.NextActionUtc.AddDays(1); }
                    else
                    {
                        Act(user, at);
                        n.Sequence++;
                        n.NextActionUtc = Next(n, at, settings.Speed);
                    }
                    if (n.NextActionUtc <= now) queue.Enqueue(user, (n.NextActionUtc, user.ID));
                }
                JsonDb.Instance.NpcManagement!.LastProcessedUtc = now;
            });
        }
    }

    private static DateTime Next(ManagedNpcState n, DateTime at, double speed)
    {
        var rng = new Random(unchecked(n.Seed ^ (int)n.Sequence * 397));
        return at.AddMinutes(Math.Max(1, rng.Next(20, 91) / speed));
    }

    private static void Act(User user, DateTime at)
    {
        var data = GameData.Instance;
        var n = user.ManagedNpc!;
        var rng = new Random(unchecked(n.Seed + (int)n.Sequence * 7919));
        int day = int.Parse(at.AddHours(-JsonDb.Instance.ResetHourUtcTime).ToString("yyyyMMdd"));
        if (day < n.Day) return;
        if (day > n.Day) { n.Day = day; n.StagesToday = n.LevelsToday = n.ActivityToday = n.ShootingToday = 0; }
        n.LastActionUtc = at; user.LastLogin = at; n.TotalActions++;
        if (n.LevelsToday < DailyTrainingLimit)
        {
            ManagedNpcGrowth.Advance(user);
            if (data.UserExpDataRecords.ContainsKey(user.userPointData.UserLevel + 1) && n.LevelsToday == 0)
                user.userPointData.UserLevel++;
            n.LevelsToday++;
            Log(n, at, user.SynchroDeviceUpgraded ? "领取养成收益，提升增强同步器" : "领取养成收益，培养基础等级");
        }
        var stages = ManagedNpcFactory.Stages;
        int current = Array.FindIndex(stages, s => s.Id == user.LastNormalStageCleared);
        if (n.StagesToday < DailyStageLimit && current >= 0 && current + 1 < stages.Length)
        {
            var next = stages[current + 1];
            long combat = user.RepresentationTeamDataNew.Sum(csn => (long)FormulaUtils.CalculateCP(user, csn));
            if (combat >= next.StandardBattlePower * .75 || next.StandardBattlePower <= 0)
            {
                ManagedNpcFactory.CompleteStage(user, next); n.StagesToday++; n.TotalStages++;
                int completed = user.FieldInfoNew.Values.Sum(f => f.CompletedStages.Count);
                user.OutpostBattleLevel.Level = Math.Min(data.OutpostBattle.Count, 1 + completed / 5);
                user.OutpostBattleLevel.Exp = completed % 5;
                Log(n, at, $"推进普通主线：第 {next.ChapterId} 章（{next.Id}）");
            }
        }
        if (at >= n.NextSkillUtc)
        {
            var main = user.Characters.Where(c => user.RepresentationTeamDataNew.Contains(c.Csn)).ToArray();
            var character = main[rng.Next(main.Length)];
            int cap = ManagedNpcRoster.SkillCap(user);
            character.Skill1Lvl = Math.Max(character.Skill1Lvl, Math.Min(cap, character.Skill1Lvl + 1));
            character.Skill2Lvl = Math.Max(character.Skill2Lvl, Math.Min(cap, character.Skill2Lvl + 1));
            character.UltimateLevel = Math.Max(character.UltimateLevel, Math.Min(cap, character.UltimateLevel + 1));
            n.NextSkillUtc = at.AddDays(5 + rng.Next(4)); Log(n, at, "培养主力角色技能");
        }
        if (at >= n.NextRecruitUtc)
        {
            var recruitment = ManagedNpcGrowth.Recruit(user, rng);
            if (recruitment != null)
            {
                ManagedNpcRoster.Apply(user);
                Log(n, at, recruitment);
            }
            n.NextRecruitUtc = at.AddDays(5 + rng.Next(5));
        }
        if (user.Guild.guildId == null && at >= n.CreatedUtc.AddDays(1))
        {
            var available = JsonDb.Instance.Guilds.Values.Where(g => g.ManagedNpcGuild &&
                data.GuildLevels.TryGetValue(g.Data.Grade, out var level) &&
                JsonDb.Instance.Users.Count(u => u.Guild.guildId == g.Data.Gsn) < level.MaxMemberCount).ToArray();
            if (available.Length > 0)
            {
                var picked = available[rng.Next(available.Length)];
                user.Guild = new GuildData { guildId = (int)picked.Data.Gsn, MemberType = 3, JoinedAt = at.Ticks, ActivityByDay = [] };
                GuildJournal.Joined(picked, user, n.Name, at);
                Log(n, at, $"加入联盟「{picked.Data.Name}」");
            }
        }
        if (user.Guild.guildId is int guildId && JsonDb.Instance.Guilds.TryGetValue(guildId, out var guild))
        {
            if (n.ActivityToday == 0)
            {
                int contribution = DailyGuildContribution;
                n.ActivityToday = Math.Max(1, GuildActivity.Record(user, contribution, at));
                Log(n, at, "参与联盟日常活动");
            }
            if (n.ShootingToday == 0 && rng.Next(4) == 0)
            { Shoot(user, guild, at, rng); n.ShootingToday = 1; }
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
