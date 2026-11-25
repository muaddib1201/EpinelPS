using EpinelPS.Data;
using EpinelPS.Database;
using Microsoft.EntityFrameworkCore;

namespace EpinelPS.LobbyServer.Mission;

internal static class AliceDiaryProgress
{
    internal const int EventId = 20002;

    internal static bool CanClaim(User user, DailyEventRecord mission, EventMissionData state, TriggerModelNew[] history)
    {
        var claimed = GameData.Instance.DailyEventTable.Values.Where(m =>
            m.EventId == EventId && state.MissionIdList.Contains(m.Id));
        var values = history.Where(t => t.Type == mission.Trigger && t.ConditionId == mission.ConditionId)
            .Select(t => (long)t.Value);
        long progress = mission.Trigger switch
        {
            Trigger.DailyEventClear => claimed.Count(m => !m.IsMain && m.EventPhaseGroupId == mission.ConditionId),
            Trigger.EventPoint => claimed.Sum(m => (long)m.PointValue),
            Trigger.CharacterLevelMax => Math.Max(values.DefaultIfEmpty().Max(),
                user.Characters.Select(c => (long)c.Level).DefaultIfEmpty().Max()),
            Trigger.UserLevel => Math.Max(values.DefaultIfEmpty().Max(), user.userPointData.UserLevel),
            _ => mission.IsStack ? values.Sum() : values.DefaultIfEmpty().Max(),
        };
        return progress >= mission.ConditionValue;
    }

    private const int AliceNameCode = 5004;

    internal static int CollectionLevel(User user)
    {
        var alice = user.Characters.Where(c => GameData.Instance.CharacterTable.GetValueOrDefault(c.Tid)?.NameCode == AliceNameCode)
            .Select(c => (long)c.Csn).ToHashSet();
        return user.FavoriteItems.Where(i => i.Csn > 0 && alice.Contains(i.Csn) &&
                GameData.Instance.FavoriteItemTable.GetValueOrDefault(i.Tid) is
                    { FavoriteType: FavoriteItemType.Collection, FavoriteRare: FavoriteItemRare.SR })
            .Select(i => i.Lv).Append(user.AliceBestSrCollectionLevel).Max();
    }

    internal static void Reconcile(User user)
    {
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!ReferenceEquals(JsonDb.GetUser(user.ID), user)) return;
            var characters = user.Characters.Where(c =>
                GameData.Instance.CharacterTable.TryGetValue(c.Tid, out var record) &&
                record.NameCode == AliceNameCode).ToArray();
            var intercept = Intercept.InterceptionService.DiaryStages(user.Interception);
            int collection = CollectionLevel(user);


            // These diary missions stack over the account's entire history. A
            // current level is evidence for that level, not another full grant
            // on every login. Keep old rows and append only the missing amount.
            List<(Trigger Type, int Condition, int Level)> levels = new()
            {
                (Type: Trigger.AliceSkill1Level, Condition: 0, Level: characters.Select(c => c.Skill1Lvl).DefaultIfEmpty().Max()),
                (Type: Trigger.AliceSkillBurstLevel, Condition: 0, Level: characters.Select(c => c.UltimateLevel).DefaultIfEmpty().Max()),
                (Type: Trigger.CharacterAttractiveLevelMax, Condition: AliceNameCode,
                    Level: characters.Length == 0 ? 0 : user.BondInfo.FirstOrDefault(b => b.NameCode == AliceNameCode)?.Lv ?? 0),
                (Type: Trigger.InterceptNormalClearWithCondition, Condition: 0, Level: intercept.Normal),
                (Type: Trigger.InterceptSpecialClearWithCondition, Condition: 0, Level: intercept.Special),
                (Type: Trigger.AliceEquipCollectionItemSR, Condition: 0, Level: collection >= 0 ? 1 : 0),
                (Type: Trigger.AliceEquipCollectionItemLevel, Condition: 0, Level: Math.Max(0, collection))
            };
            foreach (var mission in GameData.Instance.DailyEventTable.Values.Where(m =>
                m.EventId == EventId && m.Trigger == Trigger.AliceAccessAttractiveScenario).DistinctBy(m => m.ConditionId))
            {
                if (GameData.Instance.AttractiveLevelReward.GetValueOrDefault(mission.ConditionId) is
                    { NameCode: AliceNameCode, AttractiveScenarioGroupId: { Length: > 0 } scene } &&
                    user.CompletedScenarios.Contains(scene))
                    levels.Add((mission.Trigger, mission.ConditionId, 1));
            }
            using var db = GameContext.CreateNew();
            using var transaction = db.Database.BeginTransaction();
            var types = levels.Select(x => x.Type).ToArray();
            var history = db.Triggers.Where(t => t.UserId == user.ID && types.Contains(t.Type)).ToArray();
            long createdAt = DateTime.UtcNow.AddHours(9).Ticks;
            foreach (var level in levels)
            {
                long recorded = history.Where(t => t.Type == level.Type && t.ConditionId == level.Condition)
                    .Sum(t => (long)t.Value);
                if (level.Level <= recorded) continue;
                // Persist only protocol history: reconciliation never changes
                // characters, reward claims, currencies or messenger state.
                db.Triggers.Add(new TriggerModelNew
                {
                    UserId = user.ID, Type = level.Type, ConditionId = level.Condition,
                    Value = checked((int)(level.Level - recorded)), CreatedAt = createdAt
                });
            }
            db.SaveChanges();
            transaction.Commit();
        }
    }
}
