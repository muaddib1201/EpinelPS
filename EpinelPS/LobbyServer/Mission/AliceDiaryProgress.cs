using EpinelPS.Data;
using EpinelPS.Database;
using Microsoft.EntityFrameworkCore;

namespace EpinelPS.LobbyServer.Mission;

internal static class AliceDiaryProgress
{
    private const int AliceNameCode = 5004;

    internal static void Reconcile(User user)
    {
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!ReferenceEquals(JsonDb.GetUser(user.ID), user)) return;
            var characters = user.Characters.Where(c =>
                GameData.Instance.CharacterTable.TryGetValue(c.Tid, out var record) &&
                record.NameCode == AliceNameCode).ToArray();
            if (characters.Length == 0) return;

            // These diary missions stack over the account's entire history. A
            // current level is evidence for that level, not another full grant
            // on every login. Keep old rows and append only the missing amount.
            var levels = new[]
            {
                (Type: Trigger.AliceSkill1Level, Condition: 0, Level: characters.Max(c => c.Skill1Lvl)),
                (Type: Trigger.AliceSkillBurstLevel, Condition: 0, Level: characters.Max(c => c.UltimateLevel)),
                (Type: Trigger.CharacterAttractiveLevelMax, Condition: AliceNameCode,
                    Level: user.BondInfo.FirstOrDefault(b => b.NameCode == AliceNameCode)?.Lv ?? 0)
            };
            using var db = GameContext.CreateNew();
            using var transaction = db.Database.BeginTransaction();
            var history = db.Triggers.Where(t => t.UserId == user.ID &&
                ((t.Type == Trigger.AliceSkill1Level && t.ConditionId == 0) ||
                 (t.Type == Trigger.AliceSkillBurstLevel && t.ConditionId == 0) ||
                 (t.Type == Trigger.CharacterAttractiveLevelMax && t.ConditionId == AliceNameCode)))
                .ToArray();
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
