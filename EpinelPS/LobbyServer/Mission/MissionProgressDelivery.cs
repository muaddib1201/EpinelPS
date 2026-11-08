using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.Models;
using EpinelPS.Utils;
using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.Mission;

internal static class MissionProgressDelivery
{
    // JSON owns the action and outbox. SQLite delivery can be retried after a crash.
    internal static T Apply<T>(User user, Func<T> action)
    {
        lock (user)
        lock (JsonDb.SaveLock)
        {
            int index = JsonDb.Instance.Users.IndexOf(user);
            if (index < 0) throw new UnauthorizedAccessException("The account was reloaded.");
            var before = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(user))!;
            // Deserialization otherwise re-adds the User constructor's stamina
            // entry when the original currency dictionary intentionally omits it.
            before.Currency = new(user.Currency);
            var guild = GuildDirectory.Find(user.Guild.guildId ?? 0);
            var beforeGuild = guild == null ? null : JsonConvert.DeserializeObject<SavedGuild>(JsonConvert.SerializeObject(guild))!;
            T response;
            try
            {
                using (var scope = new RewardMutationScope(user))
                {
                    response = action();
                    user.PendingMissionTriggers.AddRange(scope.Triggers);
                }
                JsonDb.Save();
            }
            catch
            {
                foreach (var property in typeof(User).GetProperties().Where(p => p.CanRead && p.CanWrite))
                    property.SetValue(user, property.GetValue(before));
                foreach (var field in typeof(User).GetFields()) field.SetValue(user, field.GetValue(before));
                if (beforeGuild != null) JsonDb.Instance.Guilds[(int)beforeGuild.Data.Gsn] = beforeGuild;
                throw;
            }
            Flush(user);
            return response;
        }
    }

    internal static void Flush(User user)
    {
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.GetUser(user.ID) != user) return;
            var pending = user.PendingMissionTriggers.ToArray();
            if (pending.Length == 0) return;
            try
            {
                using var db = GameContext.CreateNew();
                var delivered = new List<TriggerModelNew>();
                foreach (var item in pending)
                {
                    var trigger = db.Triggers.FirstOrDefault(t => t.UserId == user.ID && t.Type == item.Type &&
                        t.ConditionId == item.ConditionId && t.CreatedAt == item.CreatedAt);
                    if (trigger == null)
                    {
                        trigger = new TriggerModelNew { UserId = user.ID, Type = item.Type, ConditionId = item.ConditionId,
                            CreatedAt = item.CreatedAt, Value = item.Value };
                        db.Triggers.Add(trigger);
                    }
                    delivered.Add(trigger);
                }
                db.SaveChanges();
                foreach (var trigger in delivered) MessengerMessageCreator.OnTriggerAdded(user, trigger);
                user.PendingMissionTriggers.Clear();
                JsonDb.Save();
            }
            catch (Exception ex)
            {
                user.PendingMissionTriggers = pending.ToList();
                Logging.WriteLine($"Mission notifications for user {user.ID} remain queued: {ex.Message}", LogType.Warning);
            }
        }
    }

    internal static void RecoverAll(DateTime now)
    {
        foreach (var user in JsonDb.Instance.Users.ToArray())
        {
            Flush(user);
            Lostsector.LostSectorMissionProgress.Recover(user);
            if (user.ShopMissionHistoryRepaired) continue;
            Apply(user, () =>
            {
                var start = GuildActivity.DayStart(now);
                if (user.NormalShopStates.TryGetValue((int)ShopCategoryType.ShopNormal, out var shop) &&
                    shop.RenewAt >= start.Ticks && shop.RenewAt <= now.Ticks)
                {
                    // A shop interval wholly within this game day proves these purchases belong to it.
                    int bought = shop.BuyCountsByOrder.Values.Sum();
                    using var db = GameContext.CreateNew();
                    long storedStart = start.AddHours(9).Ticks, storedEnd = start.AddDays(1).AddHours(9).Ticks;
                    int recorded = db.Triggers.Where(t => t.UserId == user.ID && t.Type == Trigger.MainShopBuy &&
                        t.CreatedAt >= storedStart && t.CreatedAt < storedEnd).ToArray()
                        .Concat(user.PendingMissionTriggers.Where(t => t.Type == Trigger.MainShopBuy &&
                            t.CreatedAt >= storedStart && t.CreatedAt < storedEnd))
                        .DistinctBy(t => (t.Type, t.ConditionId, t.CreatedAt)).Sum(t => t.Value);
                    if (bought > recorded) user.AddTrigger(Trigger.MainShopBuy, bought - recorded);
                }
                user.ShopMissionHistoryRepaired = true;
                return true;
            });
        }
    }
}
