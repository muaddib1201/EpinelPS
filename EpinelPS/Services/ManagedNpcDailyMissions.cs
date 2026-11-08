using EpinelPS.Data;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Utils;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    private static void ClaimDailyRoutineMissions(User user, DateTime now)
    {
        int beforeDaily = user.ResetableData.DailyMissionPoints;
        int beforeWeekly = user.WeeklyResetableData.WeeklyMissionPoints;
        int daily = ClaimRoutineCategory(user, HandlerType.MissionDaily, now);
        int weekly = ClaimRoutineCategory(user, HandlerType.MissionWeekly, now);
        int achievements = ClaimRoutineCategory(user, HandlerType.Achievement, now);
        user.ManagedNpc!.ActivityToday = GuildActivity.Daily(user, now);
        DailyLog(user, now, $"每日任务领取 {daily} 项（+{user.ResetableData.DailyMissionPoints - beforeDaily} 点），" +
            $"每周任务 {weekly} 项（+{user.WeeklyResetableData.WeeklyMissionPoints - beforeWeekly} 点），成就 {achievements} 项；今日联盟贡献 {user.ManagedNpc.ActivityToday}");
    }

    private static int ClaimRoutineCategory(User user, HandlerType category, DateTime now)
    {
        var definitions = GameData.Instance.TriggerTable.Values.Where(r => r.HandlerType == category)
            .OrderBy(r => r.Trigger is Trigger.PointRewardDaily or Trigger.PointRewardWeekly or Trigger.PointRewardAchievement)
            .ThenBy(r => r.Id).ToArray();
        var completed = category switch
        {
            HandlerType.MissionDaily => user.ResetableData.CompletedDailyMissions,
            HandlerType.MissionWeekly => user.WeeklyResetableData.CompletedWeeklyMissions,
            _ => user.CompletedAchievements
        };
        int total = 0;
        // A claimed task can unlock a point chest or the next achievement in this same phase.
        for (int pass = 0; pass < definitions.Length; pass++)
        {
            var history = MissionTriggerHistory.Read(user);
            int claimed = 0;
            foreach (var record in definitions)
            {
                if (completed.Contains(record.Id) || record.BeforeTriggerId != 0 && !completed.Contains(record.BeforeTriggerId)) continue;
                if (RoutineMissionProgress(user, record, history, now) < record.ConditionValue) continue;
                if (record.RewardId != 0) RewardUtils.RegisterRewardsForUser(user, record.RewardId);
                completed.Add(record.Id);
                claimed++;
                if (category == HandlerType.MissionDaily && record.PointValue > 0)
                {
                    user.ResetableData.DailyMissionPoints = checked(user.ResetableData.DailyMissionPoints + record.PointValue);
                    user.AddTrigger(Trigger.PointRewardDaily, record.PointValue);
                    GuildActivity.Record(user, record.PointValue, now);
                }
                else if (category == HandlerType.MissionWeekly && record.PointValue > 0)
                {
                    user.WeeklyResetableData.WeeklyMissionPoints = checked(user.WeeklyResetableData.WeeklyMissionPoints + record.PointValue);
                    user.AddTrigger(Trigger.PointRewardWeekly, record.PointValue);
                }
                else if (category == HandlerType.Achievement) user.AddTrigger(Trigger.PointRewardAchievement, 1);
            }
            total += claimed;
            if (claimed == 0) break;
        }
        return total;
    }

    private static long RoutineMissionProgress(User user, TriggerRecord record, TriggerModelNew[] history, DateTime now)
    {
        if (record.Trigger == Trigger.PointRewardDaily) return user.ResetableData.DailyMissionPoints;
        if (record.Trigger == Trigger.PointRewardWeekly) return user.WeeklyResetableData.WeeklyMissionPoints;
        if (record.Trigger == Trigger.PointRewardAchievement) return user.CompletedAchievements.Count;
        DateTime from = DateTime.MinValue;
        DateTime until = DateTime.MaxValue.AddHours(-9);
        if (record.HandlerType == HandlerType.MissionDaily)
        {
            from = GuildActivity.DayStart(now); until = from.AddDays(1);
        }
        else if (record.HandlerType == HandlerType.MissionWeekly)
        {
            var day = GuildActivity.DayStart(now);
            from = day.AddDays(-((int)day.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7);
            until = from.AddDays(7);
        }
        long start = from == DateTime.MinValue ? 0 : from.AddHours(9).Ticks;
        long end = until.AddHours(9).Ticks;
        var values = history.Where(t => t.Type == record.Trigger && (record.ConditionId == 0 || t.ConditionId == record.ConditionId) &&
            t.CreatedAt >= start && t.CreatedAt < end).Select(t => (long)t.Value);
        var characters = user.Characters.Where(c => GameData.Instance.CharacterTable.TryGetValue(c.Tid, out var row) &&
            (record.ConditionId == 0 || row.NameCode == record.ConditionId || c.Tid == record.ConditionId)).ToArray();
        var ownedNames = characters.Select(c => GameData.Instance.CharacterTable[c.Tid].NameCode).ToHashSet();
        return record.Trigger switch
        {
            Trigger.UserLevel => Math.Max(user.userPointData.UserLevel, values.DefaultIfEmpty().Max()),
            Trigger.CharacterLevelMax => Math.Max(user.Characters.Select(c => (long)user.GetCharacterLevel(c.Csn, c.Level)).DefaultIfEmpty().Max(), values.DefaultIfEmpty().Max()),
            Trigger.SyncroDeviceLevelMax => Math.Max(user.GetSynchroLevel(), values.DefaultIfEmpty().Max()),
            Trigger.OutpostBattleBoxLevel => Math.Max(user.OutpostBattleLevel.Level, values.DefaultIfEmpty().Max()),
            Trigger.CharacterAttractiveLevelMax => Math.Max(user.BondInfo.Where(b => ownedNames.Contains(b.NameCode))
                .Select(b => (long)b.Lv).DefaultIfEmpty().Max(), values.DefaultIfEmpty().Max()),
            Trigger.CharacterGradeMax => Math.Max(characters.Select(c => (long)Math.Clamp(c.Grade, 0, 3)).DefaultIfEmpty().Max(),
                Math.Min(3, values.DefaultIfEmpty().Max())),
            Trigger.CharacterCore => Math.Max(characters.Select(c => (long)Math.Clamp(c.Grade - 3, 0, 7)).DefaultIfEmpty().Max(), values.DefaultIfEmpty().Max()),
            Trigger.CharacterLevel or Trigger.CharacterGrade or Trigger.CharacterAttractiveLevel or
                Trigger.CharacterSkillLevel or Trigger.SpecialArenaTier or Trigger.EquipItemLevel or Trigger.RecycleResearchLevel or Trigger.HarmonyCubeLevel => values.DefaultIfEmpty().Max(),
            _ => values.Sum()
        };
    }
}
