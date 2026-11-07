using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;
using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.LobbyUser.Mail;

internal static class MailRewardClaims
{
    internal static (NetRewardData Reward, List<NetUserMailData> Claimed) Claim(User user, long? msn)
    {
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.GetUser(user.ID) != user) throw new BadHttpRequestException("Stale session.", 409);
            Flush(user);
            if (msn.HasValue && !user.MailDatas.ContainsKey(msn.Value)) throw new BadHttpRequestException("Mail not found.", 404);
            var mails = user.MailDatas.Values.Where(m => m.State == 1 && (!msn.HasValue || m.Msn == msn)).ToList();
            var reward = new NetRewardData();
            if (mails.Count == 0) return (reward, mails);
            var before = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(user))!;
            try
            {
                long at = DateTime.UtcNow.Ticks;
                using (var scope = new RewardMutationScope(user))
                {
                    foreach (var mail in mails)
                    {
                        if (mail.HasReward)
                            foreach (var item in mail.Items.Where(i => i.ExpiredAt >= at))
                                RewardUtils.AddSingleObject(user, ref reward, item.RewardId, (RewardType)item.RewardType, item.RewardValue);
                        mail.State = 2;
                    }
                    user.PendingMailRewardTriggers.AddRange(scope.Triggers);
                }
                JsonDb.Save();
            }
            catch
            {
                foreach (var p in typeof(User).GetProperties().Where(p => p.CanRead && p.CanWrite)) p.SetValue(user, p.GetValue(before));
                foreach (var f in typeof(User).GetFields()) f.SetValue(user, f.GetValue(before));
                throw;
            }
            // This outbox is already durable. Failure here must not undo a paid claim.
            Flush(user);
            return (reward, mails);
        }
    }

    private static void Flush(User user)
    {
        if (user.PendingMailRewardTriggers.Count == 0) return;
        var pending = user.PendingMailRewardTriggers;
        using var db = GameContext.CreateNew();
        foreach (var t in pending)
            if (!db.Triggers.Any(x => x.UserId == user.ID && x.Type == t.Type && x.CreatedAt == t.CreatedAt && x.ConditionId == t.ConditionId))
                db.Triggers.Add(new TriggerModelNew { UserId = t.UserId, Type = t.Type, Value = t.Value, ConditionId = t.ConditionId, CreatedAt = t.CreatedAt });
        db.SaveChanges();
        foreach (var t in pending) MessengerMessageCreator.OnTriggerAdded(user, t);
        user.PendingMailRewardTriggers = [];
        try { JsonDb.Save(); } catch { user.PendingMailRewardTriggers = pending; throw; }
    }
}
