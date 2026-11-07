using System.Security.Cryptography;
using System.Text;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Utils;
using Google.Protobuf;
using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.Character.Counsel;

internal static class CounselMissionDelivery
{
    // Called with the existing user/save locks held. Commit the counseling and its
    // notification together in JSON before delivering the notification to SQLite.
    internal static T Apply<T>(User user, IMessage request, byte[]? nonce, Func<T> apply) where T : IMessage<T>, new()
    {
        int index = JsonDb.Instance.Users.IndexOf(user);
        if (index < 0) throw new UnauthorizedAccessException("The counseling account was reloaded.");
        Flush(user);
        string? key = nonce is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData([.. Encoding.UTF8.GetBytes(typeof(T).Name), .. nonce, .. request.ToByteArray()])) : null;
        if (key != null && user.CounselMissionReceipts.FirstOrDefault(r => r.PacketKey == key) is { } receipt)
        {
            T replay = new();
            replay.MergeFrom(receipt.Response);
            return replay;
        }

        var snapshot = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(user))!;
        T response;
        try
        {
            response = apply();
            user.ResetableData.ConsumeRefilledCounsel();
            long created = Math.Max(DateTime.UtcNow.AddHours(9).Ticks,
                user.CounselMissionReceipts.Select(r => r.CreatedAt).DefaultIfEmpty(0).Max() + 1);
            user.CounselMissionReceipts.Insert(0, new CounselMissionReceipt
            {
                PacketKey = key, CreatedAt = created, Response = response.ToByteArray(), PendingTrigger = true
            });
            user.CounselMissionReceipts = user.CounselMissionReceipts.Take(32).ToList();
            JsonDb.Save();
        }
        catch
        {
            JsonDb.Instance.Users[index] = snapshot;
            throw;
        }
        Flush(user);
        return response;
    }

    private static void Flush(User user)
    {
        var pending = user.CounselMissionReceipts.Where(r => r.PendingTrigger).ToArray();
        if (pending.Length == 0) return;
        using var db = GameContext.CreateNew();
        var triggers = new List<TriggerModelNew>();
        foreach (var receipt in pending)
        {
            var trigger = db.Triggers.FirstOrDefault(t => t.UserId == user.ID && t.Type == Trigger.CharacterCounsel &&
                t.ConditionId == 0 && t.CreatedAt == receipt.CreatedAt);
            if (trigger == null)
            {
                trigger = new TriggerModelNew { UserId = user.ID, Type = Trigger.CharacterCounsel, Value = 1, CreatedAt = receipt.CreatedAt };
                db.Triggers.Add(trigger);
            }
            triggers.Add(trigger);
        }
        db.SaveChanges();
        try
        {
            foreach (var trigger in triggers) MessengerMessageCreator.OnTriggerAdded(user, trigger);
            foreach (var receipt in pending) receipt.PendingTrigger = false;
            JsonDb.Save();
        }
        catch
        {
            foreach (var receipt in pending) receipt.PendingTrigger = true;
            throw;
        }
    }
}
