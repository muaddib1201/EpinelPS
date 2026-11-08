using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Models;
using EpinelPS.Utils;
using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.Ranking;

internal static class RankingService
{
    private sealed record Snapshot(User Owner, NetWholeUserData Profile, int[] Scores, int HardChapter, bool Eligible);
    private static readonly int[] Tiers = [1, 5, 10];

    internal static int ProgressStamp(User user)
    {
        lock (user)
        lock (JsonDb.SaveLock)
        {
            var hash = new HashCode(); hash.Add(user.LastHardStageCleared);
            foreach (var character in user.Characters) { hash.Add(character.Csn); hash.Add(character.Tid); }
            return hash.ToHashCode();
        }
    }

    internal static void ObserveSafely(User? user = null)
    {
        user ??= JsonDb.Instance.Users.FirstOrDefault(u => !u.IsBanned && u.ManagedNpc == null);
        if (user == null) return;
        try { Update(user); }
        catch (Exception ex) { Logging.WriteLine("Ranking progress remains pending: " + ex.Message, LogType.Warning); }
    }

    private static void ValidateType(int type)
    {
        if (type is < 0 or > 4) throw new BadHttpRequestException("Unsupported ranking type.", 400);
    }

    internal static ResGetAllTopRank Overview(User user)
    {
        var snapshots = Settle(user);
        var response = new ResGetAllTopRank();
        for (int type = 0; type < 5; type++)
        {
            var top = Ordered(snapshots, type).FirstOrDefault();
            response.TopRankingList.Add(new NetTopRankingData { Type = type, Score = top?.Scores[type] ?? 0, User = top?.Profile.Clone() });
        }
        return response;
    }

    internal static ResGetRankingList Board(User user, int type)
    {
        ValidateType(type);
        var snapshots = Settle(user);
        var ordered = Ordered(snapshots, type).ToArray();
        var own = snapshots.Single(s => s.Owner == user);
        var response = new ResGetRankingList
        {
            User = new NetRankingData { User = own.Profile.Clone() }
        };
        for (int index = 0; index < ordered.Length; index++)
        {
            var entry = new NetRankingData { Rank = index + 1, Score = ordered[index].Scores[type], User = ordered[index].Profile.Clone() };
            if (index < 100) response.RankingList.Add(entry);
            if (ordered[index].Owner == user) response.User = entry.Clone();
        }
        lock (JsonDb.SaveLock) response.RewardEnabled = Available(user, type).Any();
        return response;
    }

    internal static ResGetRankAchievementTop Counts(User user)
    {
        Settle(user);
        lock (JsonDb.SaveLock)
        {
            var response = new ResGetRankAchievementTop();
            for (int index = 0; index < Tiers.Length; index++)
                response.TriggerList.Add(new NetTriggerData { Trigger = 31 + index, ConditionId = 0, UserValue = Count(user.Ranking!, index) });
            return response;
        }
    }

    internal static ResGetRankAchievementLeaderBoard AchievementBoard(User user, int type)
    {
        ValidateType(type);
        var snapshots = Settle(user);
        lock (JsonDb.SaveLock)
        {
            var targetIds = GameData.Instance.Rankings.Values.Where(r => (int)r.RankingType == type).Select(r => r.Id).ToHashSet();
            var response = new ResGetRankAchievementLeaderBoard();
            foreach (var snapshot in snapshots.Where(s => s.Eligible))
                foreach (var (id, achievement) in snapshot.Owner.Ranking!.Achievements.Where(a => targetIds.Contains(a.Key)))
                    response.RankAchievement.Add(new NetRankAchievementData { RankId = id, Rank = achievement.Rank,
                        Score = achievement.Score, AchievedAt = achievement.AchievedAt, User = snapshot.Profile.Clone() });
            var ordered = response.RankAchievement.OrderBy(a => a.RankId).ThenBy(a => a.Rank).ToArray();
            response.RankAchievement.Clear(); response.RankAchievement.AddRange(ordered);
            return response;
        }
    }

    internal static bool Update(User user)
    {
        Settle(user);
        lock (JsonDb.SaveLock) return Available(user, null).Any();
    }

    internal static NetRewardData Claim(User user, int? type)
    {
        if (type.HasValue) ValidateType(type.Value);
        Settle(user);
        return MissionProgressDelivery.Apply(user, () =>
        {
            var rewards = new List<NetRewardData>();
            foreach (var target in Available(user, type).ToArray())
            {
                rewards.Add(RewardUtils.RegisterRewardsForUser(user, target.ServerRewardId));
                user.Ranking!.ClaimedServerTargets.Add(target.Id);
            }
            return NetUtils.MergeRewards(rewards, user);
        });
    }

    private static IEnumerable<RankingRecord> Available(User user, int? type)
    {
        if (user.IsBanned || user.Ranking == null) return [];
        var unlocked = JsonDb.Instance.Users.Where(u => u.Ranking != null)
            .SelectMany(u => u.Ranking!.Achievements.Keys).ToHashSet();
        return GameData.Instance.Rankings.Values.Where(r => (int)r.RankingType is >= 0 and <= 4 &&
            (!type.HasValue || (int)r.RankingType == type) && r.ServerRewardId > 0 && unlocked.Contains(r.Id) &&
            !user.Ranking.ClaimedServerTargets.Contains(r.Id)).OrderBy(r => r.Id).ToArray();
    }

    private static long Count(RankingPlayerState state, int tier) =>
        state.LegacyCounts.GetValueOrDefault(31 + tier) + state.Achievements.Values.LongCount(a => a.Rank <= Tiers[tier]);

    private static IEnumerable<Snapshot> Ordered(IEnumerable<Snapshot> snapshots, int type) =>
        snapshots.Where(s => s.Eligible && s.Scores[type] > 0).OrderByDescending(s => s.Scores[type]).ThenBy(s => s.Owner.ID);

    // Snapshot each inventory before taking SaveLock. Other action writers lock the
    // player before saving, so taking another player's lock inside SaveLock would deadlock.
    private static Snapshot[] Capture()
    {
        using var db = GameContext.CreateNew();
        var names = db.Users.ToDictionary(u => u.ID, u => u.Nickname);
        var data = GameData.Instance;
        var snapshots = new List<Snapshot>();
        foreach (var user in JsonDb.Instance.Users.ToArray())
        {
            lock (user)
            lock (JsonDb.SaveLock)
            {
                var scores = new int[5];
                int chapter = 0;
                if (data.StageDataRecords.TryGetValue(user.LastHardStageCleared, out var stage) &&
                    stage.ChapterMod == ChapterMod.Hard && stage.StageType == StageType.Main)
                {
                    scores[0] = stage.Id;
                    int final = data.StageDataRecords.Values.Where(s => s.ChapterMod == ChapterMod.Hard &&
                        s.ChapterId == stage.ChapterId && s.StageType == StageType.Main).Max(s => s.Id);
                    chapter = stage.ChapterId - (stage.Id < final ? 1 : 0);
                }
                var characters = user.Characters.Select(c => data.CharacterTable.GetValueOrDefault(c.Tid)).OfType<CharacterRecord>()
                    .Select(c => (Character: c, Point: Point(c))).GroupBy(c => c.Character.NameCode).Select(g => g.MaxBy(c => c.Point));
                foreach (var character in characters)
                {
                    int type = character.Character.Corporation switch { CorporationType.MISSILIS => 1, CorporationType.ELYSION => 2,
                        CorporationType.TETRA => 3, CorporationType.PILGRIM => 4, _ => -1 };
                    if (type > 0) scores[type] = (int)Math.Min(int.MaxValue, (long)scores[type] + character.Point);
                }
                string guild = user.Guild.guildId is int guildId && JsonDb.Instance.Guilds.TryGetValue(guildId, out var union) ? union.Data.Name : "";
                var profile = new NetWholeUserData { Usn = (long)user.ID, Server = 1001, Nickname = names.GetValueOrDefault(user.ID, "Player"),
                    Lv = user.userPointData.UserLevel, Icon = user.ProfileIconId, IconPrism = user.ProfileIconIsPrism, Frame = user.ProfileFrame,
                    GuildName = guild, UserTitleId = user.TitleId, UserTitleDisplayData = new(), LastActionAt = user.LastLogin.Ticks };
                snapshots.Add(new(user, profile, scores, chapter, !user.IsBanned && names.ContainsKey(user.ID)));
            }
        }
        return snapshots.ToArray();
    }

    private static int Point(CharacterRecord character)
    {
        int baseGrade = character.OriginalRare switch { OriginalRareType.SSR => 1, OriginalRareType.SR => 101, _ => 201 };
        int value = character.GradeCoreId - baseGrade;
        if (value < 0) return 0;
        return GameData.Instance.RankingPoints.Values.FirstOrDefault(p => p.OriginalRare == character.OriginalRare &&
            p.Grade == Math.Min(value, 3) && p.Core == Math.Max(value - 3, 0))?.Point ?? 0;
    }

    private static Snapshot[] Settle(User requester)
    {
        for (int attempt = 0; ; attempt++)
        {
            var snapshots = Capture();
            lock (JsonDb.SaveLock)
            {
                if (JsonDb.GetUser(requester.ID) != requester) throw new UnauthorizedAccessException("The account was reloaded.");
                if (snapshots.Length != JsonDb.Instance.Users.Count || snapshots.Any(s => JsonDb.GetUser(s.Owner.ID) != s.Owner))
                {
                    if (attempt >= 4) throw new BadHttpRequestException("Ranking participants changed; retry the request.", 409);
                    continue;
                }
                if (GameData.Instance.Rankings.Count == 0 || GameData.Instance.RankingPoints.Count == 0)
                    throw new InvalidDataException("Ranking tables are unavailable.");
                var players = snapshots.Where(s => s.Eligible).Select(s => s.Owner).ToArray();
                if (!players.Contains(requester)) throw new UnauthorizedAccessException("Account cannot participate in rankings.");
                var states = new Dictionary<ulong, RankingPlayerState>();
                using var db = GameContext.CreateNew();
                foreach (var user in players)
                {
                    states[user.ID] = user.Ranking == null ? new RankingPlayerState
                    {
                        LegacyCounts = db.Triggers.Where(t => t.UserId == user.ID && (t.Type == Trigger.AchieveRanking1st ||
                            t.Type == Trigger.AchieveRanking5th || t.Type == Trigger.AchieveRanking10th)).ToArray()
                            .Concat(user.PendingMissionTriggers.Where(t => (int)t.Type is >= 31 and <= 33))
                            .GroupBy(t => (int)t.Type).ToDictionary(g => g.Key, g => g.Sum(t => (long)t.Value))
                    } : JsonConvert.DeserializeObject<RankingPlayerState>(JsonConvert.SerializeObject(user.Ranking))!;
                }
                var events = players.ToDictionary(u => u.ID, _ => new List<TriggerModelNew>());
                var mails = players.ToDictionary(u => u.ID, _ => new List<NetUserMailData>());
                long at = DateTime.UtcNow.Ticks;
                foreach (var target in GameData.Instance.Rankings.Values.Where(r => (int)r.RankingType is >= 0 and <= 4).OrderBy(r => r.Id))
                {
                    // Retain reserved places even if an old winner is now banned/offline.
                    var winners = JsonDb.Instance.Users.Where(u => u.Ranking?.Achievements.ContainsKey(target.Id) == true)
                        .Select(u => u.Ranking!.Achievements[target.Id].Rank).ToHashSet();
                    foreach (var state in states.Values)
                        if (state.Achievements.TryGetValue(target.Id, out var achievement)) winners.Add(achievement.Rank);
                    foreach (var snapshot in Ordered(snapshots, (int)target.RankingType))
                    {
                        var state = states[snapshot.Owner.ID];
                        if (state.Achievements.ContainsKey(target.Id)) continue;
                        bool reached = target.RankingType == RankingType.HardCampaign ? snapshot.HardChapter >= target.ConditionId :
                            snapshot.Scores[(int)target.RankingType] >= target.ConditionValue;
                        if (!reached || winners.Count >= 10) continue;
                        int rank = Enumerable.Range(1, 10).First(n => !winners.Contains(n)); winners.Add(rank);
                        state.Achievements[target.Id] = new() { Rank = rank, Score = snapshot.Scores[(int)target.RankingType], AchievedAt = at };
                        for (int index = 0; index < Tiers.Length; index++)
                        {
                            if (rank > Tiers[index]) continue;
                            state.LastTriggerAt = Math.Max(at + TimeSpan.TicksPerHour * 9, Math.Max(state.LastTriggerAt, snapshot.Owner.LastMailRewardTriggerAt) + 1);
                            events[snapshot.Owner.ID].Add(new TriggerModelNew { UserId = snapshot.Owner.ID, Type = (Trigger)(31 + index),
                                Value = 1, CreatedAt = state.LastTriggerAt });
                        }
                    }
                }
                foreach (var user in players)
                {
                    var state = states[user.ID];
                    foreach (var definition in GameData.Instance.TriggerTable.Values.Where(t => t.HandlerType == HandlerType.Ranking && (int)t.Trigger is >= 31 and <= 33))
                    {
                        if (state.MailedCumulativeRewards.Contains(definition.Id) || Count(state, (int)definition.Trigger - 31) < definition.ConditionValue) continue;
                        if (!user.CompletedAchievements.Contains(definition.Id) && user.ManagedNpc == null)
                            mails[user.ID].Add(CreateMail(user, mails[user.ID], definition, at));
                        state.MailedCumulativeRewards.Add(definition.Id);
                    }
                }
                var changed = players.Where(u => JsonConvert.SerializeObject(u.Ranking) != JsonConvert.SerializeObject(states[u.ID])).ToArray();
                var before = changed.Select(u => (User: u, u.Ranking, u.MailDatas, u.PendingMissionTriggers)).ToArray();
                try
                {
                    foreach (var user in changed)
                    {
                        user.Ranking = states[user.ID];
                        user.MailDatas = new(user.MailDatas);
                        foreach (var mail in mails[user.ID]) user.MailDatas.Add(mail.Msn, mail);
                        user.PendingMissionTriggers = [.. user.PendingMissionTriggers, .. events[user.ID]];
                    }
                    if (changed.Length > 0) JsonDb.Save();
                }
                catch
                {
                    foreach (var previous in before)
                    {
                        previous.User.Ranking = previous.Ranking; previous.User.MailDatas = previous.MailDatas;
                        previous.User.PendingMissionTriggers = previous.PendingMissionTriggers;
                    }
                    throw;
                }
            }
            // The milestone, its mail and its notification outbox are now durable.
            foreach (var snapshot in snapshots.Where(s => s.Owner.PendingMissionTriggers.Count > 0)) MissionProgressDelivery.Flush(snapshot.Owner);
            return snapshots;
        }
    }

    private static NetUserMailData CreateMail(User user, List<NetUserMailData> pending, TriggerRecord definition, long ticks)
    {
        var reward = GameData.Instance.RewardDataRecords.GetValueOrDefault(definition.RewardId)
            ?? throw new InvalidDataException("Missing ranking reward " + definition.RewardId);
        long msn = User.GenerateMsn();
        while (user.MailDatas.ContainsKey(msn) || pending.Any(m => m.Msn == msn)) msn++;
        var at = new DateTime(ticks, DateTimeKind.Utc);
        var mail = new NetUserMailData { Msn = msn, Sender = 1, Type = 1, State = 1, HasReward = true, Period = 90, CreatedAt = ticks,
            Title = new() { IsPlain = true, Str = "排名特典奖励" },
            Text = new() { IsPlain = true, Str = $"累计达成前 {Tiers[(int)definition.Trigger - 31]} 名目标 {definition.ConditionValue} 次的奖励。" } };
        foreach (var item in reward.Rewards.Where(r => r.RewardType != RewardType.None))
            mail.Items.Add(new NetMailRewardItem { RewardType = (int)item.RewardType, RewardId = item.RewardId, RewardValue = item.RewardValue, ExpiredAt = at.AddDays(90).Ticks });
        return mail;
    }
}
