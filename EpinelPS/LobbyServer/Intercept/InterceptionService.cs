using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Utils;
using Google.Protobuf;
using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.Intercept;

internal sealed class InterceptionService(User user, DateTime now, byte[]? nonce)
{
    private int Day => int.Parse(now.AddHours(-JsonDb.Instance.ResetHourUtcTime).ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    private static string BossKey(int type, int id) => $"{type}:{id}";

    internal ResGetInterceptData Get()
    {
        FlushTriggers();
        var bosses = GameData.Instance.InterceptSpecial.Values.Where(b => b.Group == 1).OrderBy(b => b.Order).ToArray();
        return new ResGetInterceptData
        {
            NormalInterceptGroup = 1, SpecialInterceptId = bosses[now.DayOfYear % bosses.Length].Id,
            TicketCount = user.ResetableData.InterceptionTickets, MaxTicketCount = JsonDb.Instance.MaxInterceptionCount
        };
    }

    internal bool HasRecord(int type, int id)
    {
        ValidateBoss(type, id);
        return Best(type, id) != null;
    }

    private InterceptionBest? Best(int type, int id) => user.Interception?.BestByBoss.GetValueOrDefault(BossKey(type, id)) is { } best && best.Day == Day ? best : null;

    internal ResClearIntercept Clear(ReqClearIntercept req)
    {
        string? key = PacketKey("clear", req);
        FlushTriggers();
        if (Replay(key) is { } replay) return ResClearIntercept.Parser.ParseFrom(replay);
        ValidateBoss(req.Intercept, req.InterceptId);
        if (req.Damage < 0) throw new BadHttpRequestException("Interception damage cannot be negative.", 400);
        return Commit(key, req.Intercept, req.InterceptId, req.Damage, false, reward => new ResClearIntercept
        {
            Intercept = req.Intercept, InterceptId = req.InterceptId, NormalReward = reward.NormalReward,
            BonusReward = reward.BonusReward, TicketCount = user.ResetableData.InterceptionTickets,
            MaxTicketCount = JsonDb.Instance.MaxInterceptionCount
        });
    }

    internal ResFastClearIntercept FastClear(ReqFastClearIntercept req)
    {
        string? key = PacketKey("fastclear", req);
        FlushTriggers();
        if (Replay(key) is { } replay) return ResFastClearIntercept.Parser.ParseFrom(replay);
        ValidateBoss(req.Intercept, req.InterceptId);
        if (Best(req.Intercept, req.InterceptId) is not { } best)
            return new ResFastClearIntercept { Result = FastClearResult.NoRecordToday,
                TicketCount = user.ResetableData.InterceptionTickets, MaxTicketCount = JsonDb.Instance.MaxInterceptionCount };
        return Commit(key, req.Intercept, req.InterceptId, best.Damage, true, reward => new ResFastClearIntercept
        {
            Damage = best.Damage, NormalReward = reward.NormalReward, BonusReward = reward.BonusReward,
            TicketCount = user.ResetableData.InterceptionTickets, MaxTicketCount = JsonDb.Instance.MaxInterceptionCount
        });
    }

    internal static InterceptionClearResult SettleNpcRoutine(User user, DateTime now, int type, int id, long damage)
    {
        if (user.ManagedNpc == null || !RewardMutationScope.DefersSave) throw new InvalidOperationException("NPC phase transaction required.");
        ValidateBoss(type, id);
        if (user.ResetableData.InterceptionTickets <= 0) throw new BadHttpRequestException("No interception attempts remain.", 400);
        var reward = InterceptionHelper.Clear(user, type, id, damage);
        user.ResetableData.InterceptionTickets--;
        var state = user.Interception ??= new();
        int day = LobbyServer.Guild.GuildActivity.Day(now);
        var key = BossKey(type, id);
        long previous = state.BestByBoss.GetValueOrDefault(key) is { } saved && saved.Day == day ? saved.Damage : 0;
        state.BestByBoss[key] = new InterceptionBest { Day = day, Damage = Math.Max(previous, damage) };
        user.AddTrigger(Trigger.InterceptStart, 1);
        user.AddTrigger(Trigger.InterceptClear, 1);
        return reward;
    }

    private T Commit<T>(string? key, int type, int id, long damage, bool fast, Func<InterceptionClearResult, T> response) where T : IMessage<T>
    {
        if (user.ResetableData.InterceptionTickets <= 0) throw new BadHttpRequestException("No interception attempts remain.", 400);
        // Reward registration can touch multiple inventory/currency fields: restore the whole
        // account, rather than trying to guess which nested objects a reward will mutate.
        var snapshot = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(user))!;
        int index = JsonDb.Instance.Users.IndexOf(user);
        T result;
        try
        {
            var reward = InterceptionHelper.Clear(user, type, id, damage);
            user.ResetableData.InterceptionTickets--;
            var progress = user.Interception ??= new();
            if (!fast)
                progress.BestByBoss[BossKey(type, id)] = new InterceptionBest { Day = Day, Damage = Math.Max(Best(type, id)?.Damage ?? 0, damage) };
            result = response(reward);
            long created = Math.Max(now.AddHours(9).Ticks,
                progress.Receipts.Select(r => r.CreatedAt).DefaultIfEmpty(0).Max() + 1);
            // Even nonce-less requests carry an outbox until mission progress is committed.
            var receipt = new InterceptionReceipt { Key = key, CreatedAt = created, Response = result.ToByteArray(), PendingTriggers =
                [new TriggerModelNew { UserId = user.ID, Type = Trigger.InterceptClear, Value = 1, CreatedAt = created }] };
            if (fast) receipt.PendingTriggers.Add(new TriggerModelNew { UserId = user.ID, Type = Trigger.InterceptStart, Value = 1, CreatedAt = created });
            progress.Receipts.Insert(0, receipt);
            progress.Receipts = progress.Receipts.Take(64).ToList();
            JsonDb.Save();
        }
        catch
        {
            JsonDb.Instance.Users[index] = snapshot;
            throw;
        }
        // A failure here leaves the paid clear and its receipt committed. Retrying flushes
        // the outbox before replaying the receipt; it never grants rewards a second time.
        FlushTriggers();
        Logging.WriteLine($"[Interception] user={user.ID} type={type} boss={id} fast={fast} damage={damage} tickets={user.ResetableData.InterceptionTickets}");
        return result;
    }

    private string? PacketKey(string route, IMessage request) => nonce is { Length: > 0 }
        ? Convert.ToHexString(SHA256.HashData([.. Encoding.ASCII.GetBytes(route), .. nonce, .. request.ToByteArray()])) : null;

    private byte[]? Replay(string? key) => key == null ? null : user.Interception?.Receipts.FirstOrDefault(r => r.Key == key)?.Response;

    private void FlushTriggers()
    {
        var pending = user.Interception?.Receipts.Where(r => r.PendingTriggers.Count != 0)
            .Select(r => (Receipt: r, Triggers: r.PendingTriggers)).ToArray() ?? [];
        if (pending.Length == 0) return;
        using var db = GameContext.CreateNew();
        try
        {
            foreach (var (receipt, triggers) in pending)
            {
                foreach (var trigger in triggers)
                    if (!db.Triggers.Any(t => t.UserId == user.ID && t.Type == trigger.Type && t.ConditionId == trigger.ConditionId && t.CreatedAt == trigger.CreatedAt))
                        db.Triggers.Add(new TriggerModelNew { UserId = user.ID, Type = trigger.Type, ConditionId = trigger.ConditionId, CreatedAt = trigger.CreatedAt, Value = trigger.Value });
                db.SaveChanges();
                foreach (var trigger in triggers) MessengerMessageCreator.OnTriggerAdded(user, trigger);
                receipt.PendingTriggers = [];
            }
            JsonDb.Save();
        }
        catch
        {
            foreach (var (receipt, triggers) in pending) receipt.PendingTriggers = triggers;
            throw;
        }
    }

    private static void ValidateBoss(int type, int id)
    {
        bool valid = type switch
        {
            0 or 1 => GameData.Instance.InterceptNormal.TryGetValue(id, out var normal) && (int)normal.Type == type,
            2 => GameData.Instance.InterceptSpecial.ContainsKey(id),
            _ => false
        };
        if (!valid) throw new BadHttpRequestException("Unknown interception difficulty or boss.", 400);
    }
}
