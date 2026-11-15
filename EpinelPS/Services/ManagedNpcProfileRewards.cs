using EpinelPS.Data;
using EpinelPS.Database;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    public static void RefreshProfileRewards(GameContext db)
    {
        lock (JsonDb.SaveLock)
        {
            var npcs = JsonDb.Instance.Users.Where(u => u.ManagedNpc != null).ToArray();
            if (npcs.Length == 0) return;
            var ids = npcs.Select(u => u.ID).ToArray();
            // Count committed actions. Pending deliveries become eligible on a later tick,
            // including after a restart between SQLite delivery and clearing the JSON outbox.
            var totals = db.Triggers.Where(t => ids.Contains(t.UserId) &&
                    (t.Type == Trigger.SendFriendShipPoint || t.Type == Trigger.RookieArenaPlayCount))
                .GroupBy(t => new { t.UserId, t.Type })
                .Select(g => new { g.Key.UserId, g.Key.Type, Count = g.Sum(t => (long)t.Value) })
                .ToDictionary(r => (r.UserId, r.Type), r => r.Count);
            var catalogue = new ProfileRewardCatalogue(GameData.Instance);
            var previous = new List<(User User, NpcProfileInventory Inventory)>();
            try
            {
                foreach (var user in npcs)
                {
                    var earned = catalogue.EarnedBy(user, totals.GetValueOrDefault((user.ID, Trigger.SendFriendShipPoint)),
                        totals.GetValueOrDefault((user.ID, Trigger.RookieArenaPlayCount)));
                    if (earned.Matches(user)) continue;
                    previous.Add((user, NpcProfileInventory.Read(user)));
                    earned.Apply(user);
                }
                if (previous.Count > 0) JsonDb.Save();
            }
            catch
            {
                foreach (var (user, inventory) in previous) inventory.Apply(user);
                throw;
            }
        }
    }

    private sealed record NpcProfileInventory(List<int> Titles, List<int> Frames, int Title, int Frame)
    {
        public static NpcProfileInventory Read(User user) => new(user.TitleList, user.FrameList, user.TitleId, user.ProfileFrame);
        public bool Matches(User user) => Title == user.TitleId && Frame == user.ProfileFrame &&
            Titles.SequenceEqual(user.TitleList) && Frames.SequenceEqual(user.FrameList);
        public void Apply(User user) => (user.TitleList, user.FrameList, user.TitleId, user.ProfileFrame) = (Titles, Frames, Title, Frame);
    }

    private sealed class ProfileRewardCatalogue(GameData data)
    {
        private const int DefaultId = 1;
        private readonly (int Id, UserTitleConditionObject[] Requirements)[] missions = data.UserTitleAcquireConditionTable.Values
            .GroupBy(r => r.UserTitleId)
            .Where(g => g.All(r => r.InfoType == UserTitleAcquireInformationType.Mission) &&
                data.userTitleRecords.TryGetValue(g.Key, out var title) &&
                data.RewardDataRecords.TryGetValue(title.RewardId, out var reward) &&
                reward.Rewards.Any(i => i.RewardType == RewardType.UserTitle && i.RewardId == g.Key && i.RewardValue > 0))
            .Select(g => (g.Key, g.SelectMany(r => r.AcquireConditionList)
                .Where(c => c.ConditionType != UserTitleAcquireConditionType.None).ToArray()))
            .ToArray();
        private readonly Dictionary<int, int> chapterBosses = data.StageDataRecords.Values
            .Where(s => s.ChapterMod == ChapterMod.Normal && s.StageType == StageType.Main && s.StageCategory == StageCategory.Boss)
            .GroupBy(s => s.ChapterId).Where(g => data.ChapterCampaignData.ContainsKey(g.Key))
            .ToDictionary(g => data.ChapterCampaignData[g.Key].Id, g => g.Max(s => s.Id));
        private readonly HashSet<(CorporationTowerType, int)> towerFloors = data.towerTable.Values.Select(t => (t.Type, t.Floor)).ToHashSet();
        private readonly HashSet<string> episodes = data.AttractiveLevelReward.Values.Select(r => r.AttractiveScenarioGroupId)
            .OfType<string>().Where(id => id.Length > 0).ToHashSet();

        public NpcProfileInventory EarnedBy(User user, long friendPointsSent = 0, long rookieBattles = 0)
        {
            if (!data.userTitleRecords.ContainsKey(DefaultId) || !data.userFrameTable.ContainsKey(DefaultId))
                return NpcProfileInventory.Read(user);
            var titles = user.TitleList.ToHashSet();
            var frames = user.FrameList.ToHashSet();
            titles.Add(DefaultId);
            frames.Add(DefaultId);
            foreach (var (id, requirements) in missions)
                if (!titles.Contains(id) && requirements.Length > 0 && requirements.All(Meets))
                    titles.Add(id);

            // Ranking settlement records the cumulative milestones for NPCs as well as humans.
            // NPCs historically received this receipt without the corresponding player mail.
            if (user.Ranking != null)
                foreach (int id in user.Ranking.MailedCumulativeRewards)
                {
                    if (!data.TriggerTable.TryGetValue(id, out var milestone) || milestone.HandlerType != HandlerType.Ranking ||
                        !data.RewardDataRecords.TryGetValue(milestone.RewardId, out var reward)) continue;
                    foreach (var item in reward.Rewards)
                        if (item.RewardType == RewardType.Frame && item.RewardValue > 0 && data.userFrameTable.ContainsKey(item.RewardId))
                            frames.Add(item.RewardId);
                }

            var ownedTitles = titles.Order().ToList();
            var ownedFrames = frames.Order().ToList();
            return new(ownedTitles, ownedFrames, Choose(ownedTitles, data.userTitleRecords.ContainsKey),
                Choose(ownedFrames, data.userFrameTable.ContainsKey));

            int Choose(List<int> owned, Func<int, bool> inCatalogue)
            {
                var choices = owned.Where(id => id != DefaultId && inCatalogue(id)).ToArray();
                return choices.Length == 0 ? DefaultId : choices[(uint)user.ManagedNpc!.Seed % choices.Length];
            }

            bool Meets(UserTitleConditionObject condition)
            {
                int value = condition.ConditionPointValue;
                if (value <= 0) return false;
                return condition.ConditionType switch
                {
                    UserTitleAcquireConditionType.UserTitleUserLv => user.userPointData.UserLevel >= value,
                    UserTitleAcquireConditionType.UserTitleInfracoreLv => user.InfraCoreLvl >= value,
                    UserTitleAcquireConditionType.UserTitleOpenSyncroDevice => value == 1 && user.SynchroDeviceUpgraded,
                    UserTitleAcquireConditionType.UserTitleClearChapter => chapterBosses.TryGetValue(value, out int boss) && user.IsStageCompleted(boss),
                    UserTitleAcquireConditionType.UserTitleOutpostScenario => user.ClearedOutpostScenarioIds.Distinct().Count() >= value,
                    UserTitleAcquireConditionType.UserTitleAttractiveScenario => user.CompletedScenarios.Intersect(episodes).Count() >= value,
                    UserTitleAcquireConditionType.UserTitleSendFriendPointCount => friendPointsSent >= value,
                    UserTitleAcquireConditionType.UserTitleRookieArenaPlayCount => rookieBattles >= value,
                    UserTitleAcquireConditionType.UserTitleLostSectorPerfectClearCount => user.LostSectorData.Values.Count(s => s.CompletedPerfectly) >= value,
                    UserTitleAcquireConditionType.UserTitleTowerClearTotalCount => user.TowerProgress
                        .Where(t => towerFloors.Contains((t.Key, t.Value))).Sum(t => t.Value) >= value,
                    UserTitleAcquireConditionType.UserTitleAcquireCharacter => user.LiberateDatas.Values.Any(l => l.IsCompleted &&
                        data.LiberateCharacterTable.TryGetValue(l.CharacterId, out var character) && character.CharacterNameCode == value),
                    UserTitleAcquireConditionType.UserTitleRecycleResearchAttacker => Researched(RecycleSubType.Attacker),
                    UserTitleAcquireConditionType.UserTitleRecycleResearchDefender => Researched(RecycleSubType.Defender),
                    UserTitleAcquireConditionType.UserTitleRecycleResearchSupporter => Researched(RecycleSubType.Supporter),
                    UserTitleAcquireConditionType.UserTitleRecycleResearchElysion => Researched(RecycleSubType.ELYSION),
                    UserTitleAcquireConditionType.UserTitleRecycleResearchMissilis => Researched(RecycleSubType.MISSILIS),
                    UserTitleAcquireConditionType.UserTitleRecycleResearchTetra => Researched(RecycleSubType.TETRA),
                    UserTitleAcquireConditionType.UserTitleRecycleResearchPilgrim => Researched(RecycleSubType.PILGRIM),
                    UserTitleAcquireConditionType.UserTitleRecycleResearchAbnormal => Researched(RecycleSubType.ABNORMAL),
                    _ => false
                };

                bool Researched(RecycleSubType type) => user.ResearchProgress.Any(r => r.Value.Level >= value &&
                    data.RecycleResearchStats.TryGetValue(r.Key, out var research) && research.RecycleSubType == type);
            }
        }
    }
}
