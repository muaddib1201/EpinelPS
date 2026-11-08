using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Arena;
using EpinelPS.LobbyServer.Intercept;
using EpinelPS.LobbyServer.Simroom;
using EpinelPS.LobbyServer.Stage;
using EpinelPS.LobbyServer.Tower;
using EpinelPS.Utils;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    private static void RecruitAndResourceBattlesDaily(User user, DateTime now)
    {
        RecruitDaily(user, now);
        if (DailyUnlocked(user, ContentsOpen.SimulationRoom))
        {
            long combat = DailyCombat(user);
            var chapter = GameData.Instance.SimulationRoomChapterTable.Values.Where(c => !c.OverclockActive &&
                    c.RecommendedBattlePower * .75 <= combat &&
                    GameData.Instance.SimulationRoomDifficultyTable.TryGetValue(c.DifficultyId, out var difficulty) &&
                    DailyCondition(user, difficulty.OpenConditionType, difficulty.OpenConditionValue))
                .OrderBy(c => c.DifficultyId).ThenBy(c => c.Chapter).LastOrDefault();
            if (chapter != null)
            {
                var reward = SimRoomHelper.SimRoomReceivedReward(user, chapter.DifficultyId, chapter.Chapter);
                if (reward != null)
                {
                    user.ResetableData.SimRoomData.CurrentDifficulty = chapter.DifficultyId;
                    user.ResetableData.SimRoomData.CurrentChapter = chapter.Chapter;
                    user.ResetableData.ClearedSimulationRoom = true;
                    user.AddTrigger(Trigger.SimulationRoomStart, 1);
                    SimRoomHelper.RecordClear(user);
                }
                DailyLog(user, now, $"模拟室 {chapter.DifficultyId}-{chapter.Chapter}：可领取奖励已入账");
            }
            else DailyLog(user, now, "模拟室：暂无符合解锁和战力条件的难度");
        }
        else DailyLog(user, now, "模拟室：尚未解锁");
        int type, boss;
        if (DailyUnlocked(user, ContentsOpen.InterceptSpecial))
        {
            var bosses = GameData.Instance.InterceptSpecial.Values.Where(b => b.Group == 1).OrderBy(b => b.Order).ToArray();
            type = 2; boss = bosses.Length == 0 ? 0 : bosses[now.DayOfYear % bosses.Length].Id;
        }
        else
        {
            type = DailyUnlocked(user, ContentsOpen.InterceptNormalLevel2) ? 1 : 0;
            boss = DailyUnlocked(user, ContentsOpen.InterceptNormalLevel1) ?
                GameData.Instance.InterceptNormal.Values.Where(b => (int)b.Type == type).OrderBy(b => b.Order).Select(b => b.Id).FirstOrDefault() : 0;
        }
        if (boss == 0) { DailyLog(user, now, "拦截战：尚未解锁或没有可用首领"); return; }
        int attempts = 0;
        while (user.ResetableData.InterceptionTickets > 0)
        {
            // Resolve only a damage tier from the owned team's power; no battle engine runs.
            long damage = Math.Max(0, DailyCombat(user) * 180);
            InterceptionService.SettleNpcRoutine(user, now, type, boss, damage);
            attempts++;
        }
        DailyLog(user, now, $"拦截战完成 {attempts} 次，次数已扣除，奖励已入账");
    }

    private static void AdvanceDailyProgress(User user, DateTime now)
    {
        var stages = GameData.Instance.StageDataRecords.Values.Where(s => s.ChapterMod == ChapterMod.Normal && s.StageType == StageType.Main &&
            s.StageCategory is StageCategory.Normal or StageCategory.Boss or StageCategory.Story).OrderBy(s => s.ChapterId).ThenBy(s => s.Id).ToArray();
        int current = Array.FindIndex(stages, s => s.Id == user.LastNormalStageCleared), advanced = 0;
        if (current >= 0)
            foreach (var next in stages.Skip(current + 1))
            {
                if (user.ManagedNpc!.StagesToday >= DailyStageLimit) break;
                if (user.IsStageCompleted(next.Id)) continue;
                if (next.StandardBattlePower > 0 && DailyCombat(user) < next.StandardBattlePower * .75) break;
                user.AddTrigger(Trigger.CampaignStart, 1, next.Id);
                ClearStage.CompleteStage(user, next.Id, forceCompleteScenarios: true, save: false);
                user.ManagedNpc.StagesToday++; user.ManagedNpc.TotalStages++; advanced++;
            }
        int questRewards = 0;
        foreach (int id in user.MainQuestData.Where(q => !q.Value).Select(q => q.Key).ToArray())
        {
            var quest = GameData.Instance.GetMainQuestByTableId(id);
            if (quest == null || quest.RewardId == 0) continue;
            RewardUtils.RegisterRewardsForUser(user, quest.RewardId);
            user.MainQuestData[id] = true;
            questRewards++;
        }
        DailyLog(user, now, $"主线推进 {advanced} 关，领取主线任务 {questRewards} 项，剧情状态已保存");
        int towers = 0;
        int weekday = (int)LobbyServer.Guild.GuildActivity.DayStart(now).AddDays(1).DayOfWeek;
        foreach (var info in GameData.Instance.TowerInfoTable.Values.OrderBy(i => (int)i.Id))
        {
            if (!DailyUnlocked(user, info.Id == CorporationTowerType.ALL ? ContentsOpen.Tower : ContentsOpen.CompanyTower)) continue;
            if (info.DayOfWeek?.Contains(weekday) != true) continue;
            var team = user.Characters.Where(c => info.Id == CorporationTowerType.ALL ||
                    (int)GameData.Instance.CharacterTable[c.Tid].Corporation == (int)info.Id)
                .OrderByDescending(c => FormulaUtils.CalculateCP(user, c.Csn)).Take(5).ToArray();
            long combat = team.Sum(c => (long)FormulaUtils.CalculateCP(user, c.Csn));
            if (team.Length == 0) continue;
            int limit = info.ClearLimit > 0 ? Math.Max(0, info.ClearLimit - user.ResetableData.TowerCount.GetValueOrDefault(info.Id)) : 3;
            for (int i = 0; i < limit; i++)
            {
                int floor = user.TowerProgress.GetValueOrDefault(info.Id) + 1;
                var next = GameData.Instance.towerTable.Values.FirstOrDefault(t => t.Type == info.Id && t.Floor == floor);
                if (next == null || next.StandardBattlePower > 0 && combat < next.StandardBattlePower * .75) break;
                user.AddTrigger(Trigger.TowerAllStart, 1);
                TowerHelper.CompleteTower(user, next.Id);
                towers++;
            }
        }
        DailyLog(user, now, $"爬塔完成 {towers} 层，首通奖励已结算；未开放或战力不足的项目跳过");
    }

    private static void CompleteDailySocial(User user, DateTime now)
    {
        if (DailyUnlocked(user, ContentsOpen.Arena))
        {
            var arena = new ArenaService(user, now, null);
            int attempts = arena.RunNpcRoutine();
            DailyLog(user, now, $"竞技场完成 {attempts} 次 NPC 对战，次数、排名、战绩和奖励已结算");
        }
        else DailyLog(user, now, "竞技场：尚未解锁");
        if (user.Guild.guildId is int guildId && JsonDb.Instance.Guilds.TryGetValue(guildId, out var guild) && user.ManagedNpc!.ShootingToday == 0)
        {
            Shoot(user, guild, now, new Random(unchecked(user.ManagedNpc.Seed ^ user.ManagedNpc.Day)));
            user.ManagedNpc.ShootingToday = 1;
        }
    }
}
