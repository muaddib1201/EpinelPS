using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Services;
using EpinelPS.Utils;
using Google.Protobuf;
using System.Security.Cryptography;
using System.Text;

namespace EpinelPS.LobbyServer.Friend;

public static class FriendshipService
{
    public const int Capacity = 30;
    private const int DailyLimit = 30;

    internal static int Seed(User user)
    {
        var state = State(user);
        if (state.NpcFriendsInitialized) return 0;
        int added = 0;
        var candidates = JsonDb.Instance.Users.Where(u => u.ID != user.ID && u.ManagedNpc is { Enabled: true } &&
                !state.Friends.Contains(u.ID) && (u.Friendships?.Friends.Count ?? 0) < Capacity)
            .OrderByDescending(Combat).ThenByDescending(u => u.userPointData.UserLevel).ThenBy(u => u.ID).ToArray();
        foreach (var npc in candidates.Take(Math.Max(0, Capacity - state.Friends.Count)))
        {
            state.Friends.Add(npc.ID);
            State(npc).Friends.Add(user.ID);
            added++;
        }
        state.NpcFriendsInitialized = true;
        return added;
    }

    public static ResGetFriendData Get(User user, GameContext db, DateTime now) => Change(user, () =>
    {
        RefreshIncoming(user, now);
        var state = State(user);
        var result = new ResGetFriendData { SendFriendshipCount = state.SentCount, ReceiveFriendshipCount = state.ReceivedCount };
        foreach (var friend in Friends(user))
        {
            int receive = CanClaim(user, friend) ? FriendshipWire.Available
                : History(user, friend.ID).ClaimedDay == state.Day ? FriendshipWire.Claimed : FriendshipWire.NoGift;
            result.Friend.Add(new NetUserFriendData { State = FriendshipWire.Accepted, Type = FriendshipWire.Normal,
                User = Profile(friend, db), IsSend = History(user, friend.ID).SentDay == state.Day, ReceiveState = receive });
        }
        // Current-day gifts are represented per friend; no older-day bank is synthesized.
        result.TodayFriendshipCount = result.Friend.Count(f => f.ReceiveState == FriendshipWire.Available);
        return result;
    });

    public static ResSendFriendshipPoint Send(User user, ulong friendId, DateTime now) => Send(user, friendId, now, null);
    internal static ResSendFriendshipPoint Send(User user, ulong friendId, DateTime now, byte[]? nonce) => Change(user, () =>
    {
        Prepare(user, now);
        var friend = FindFriend(user, friendId);
        if (friend == null) return new ResSendFriendshipPoint { Result = FriendshipPointResult.FriendNotExist };
        Prepare(friend, now);
        bool sent = SendOne(user, friend);
        if (sent && user.ManagedNpc == null) user.AddTrigger(Trigger.SendFriendShipPoint, 1);
        return new ResSendFriendshipPoint { Result = sent ? FriendshipPointResult.Success : FriendshipPointResult.Expired };
    }, ReceiptKey(nameof(Send), friendId, nonce));

    public static ResObtainFriendshipPoint Obtain(User user, ulong friendId, DateTime now) => Obtain(user, friendId, now, null);
    internal static ResObtainFriendshipPoint Obtain(User user, ulong friendId, DateTime now, byte[]? nonce) => Change(user, () =>
    {
        RefreshIncoming(user, now);
        var friend = FindFriend(user, friendId);
        if (friend == null) return new ResObtainFriendshipPoint { Result = FriendshipPointResult.FriendNotExist };
        bool claimed = ClaimOne(user, friend);
        if (claimed && user.ManagedNpc == null) user.AddTrigger(Trigger.GetFriendShipPoint, 1);
        return new ResObtainFriendshipPoint { Result = claimed ? FriendshipPointResult.Success : FriendshipPointResult.Expired };
    }, ReceiptKey(nameof(Obtain), friendId, nonce));

    public static ResAllFriendshipPoint All(User user, GameContext db, DateTime now) => All(user, db, now, null);
    internal static ResAllFriendshipPoint All(User user, GameContext db, DateTime now, byte[]? nonce) => Change(user, () =>
    {
        RefreshIncoming(user, now);
        var result = new ResAllFriendshipPoint();
        foreach (var friend in Friends(user))
        {
            Prepare(friend, now);
            if (SendOne(user, friend)) result.SendCount++;
            if (ClaimOne(user, friend)) { result.ReceiveCount++; result.Senders.Add(Profile(friend, db)); }
        }
        if (user.ManagedNpc == null)
        {
            if (result.SendCount > 0) user.AddTrigger(Trigger.SendFriendShipPoint, result.SendCount);
            if (result.ReceiveCount > 0) user.AddTrigger(Trigger.GetFriendShipPoint, result.ReceiveCount);
        }
        return result;
    }, ReceiptKey(nameof(All), 0, nonce));

    public static ResDeleteFriend Delete(User user, ulong friendId, DateTime now) => Delete(user, friendId, now, null);
    internal static ResDeleteFriend Delete(User user, ulong friendId, DateTime now, byte[]? nonce) => Change(user, () =>
    {
        var friend = FindFriend(user, friendId);
        if (friend == null) return new ResDeleteFriend { Result = DeleteFriendResult.FriendNotExist };
        State(user).Friends.Remove(friend.ID);
        State(friend).Friends.Remove(user.ID);
        return new ResDeleteFriend();
    }, ReceiptKey(nameof(Delete), friendId, nonce));

    internal static (int Sent, int Received) SettleNpcDaily(User user, DateTime now)
    {
        if (user.ManagedNpc == null) throw new ArgumentException("Managed NPC required.");
        RefreshIncoming(user, now);
        int sent = 0, received = 0;
        foreach (var friend in Friends(user))
        {
            Prepare(friend, now);
            if (SendOne(user, friend)) sent++;
            if (ClaimOne(user, friend)) received++;
        }
        if (sent > 0) user.AddTrigger(Trigger.SendFriendShipPoint, sent);
        if (received > 0) user.AddTrigger(Trigger.GetFriendShipPoint, received);
        return (sent, received);
    }

    private static FriendshipState State(User user) => user.Friendships ??= new();
    private static FriendGiftHistory History(User user, ulong id)
    {
        var state = State(user);
        if (!state.Gifts.TryGetValue(id, out var history)) state.Gifts[id] = history = new();
        return history;
    }

    private static int Combat(User user) => (int)Math.Min(int.MaxValue,
        user.RepresentationTeamDataNew.Sum(c => (long)FormulaUtils.CalculateCP(user, c)));

    private static void Prepare(User user, DateTime now)
    {
        var state = State(user);
        int day = GuildActivity.Day(now);
        if (day <= state.Day) return;
        state.Day = day;
        state.SentCount = state.ReceivedCount = 0;
    }

    private static User? FindFriend(User user, ulong id) => id != user.ID && State(user).Friends.Contains(id) &&
        JsonDb.GetUser(id) is {} friend && friend.Friendships?.Friends.Contains(user.ID) == true ? friend : null;

    private static IEnumerable<User> Friends(User user) => State(user).Friends.Order().Select(id => FindFriend(user, id)).OfType<User>();

    private static void RefreshIncoming(User user, DateTime now)
    {
        Prepare(user, now);
        if (JsonDb.Instance.NpcManagement?.Enabled != true) return;
        foreach (var friend in Friends(user).Where(u => u.ManagedNpc is { Enabled: true }))
        {
            Prepare(friend, now);
            SendOne(friend, user);
        }
    }

    private static bool SendOne(User sender, User receiver)
    {
        var state = State(sender);
        var history = History(sender, receiver.ID);
        if (state.Day == 0 || state.Day < State(receiver).Day || state.SentCount >= DailyLimit || history.SentDay >= state.Day) return false;
        history.SentDay = state.Day;
        state.SentCount++;
        return true;
    }

    private static bool CanClaim(User receiver, User sender)
    {
        var state = State(receiver);
        return state.Day > 0 && state.ReceivedCount < DailyLimit && History(sender, receiver.ID).SentDay == state.Day &&
            History(receiver, sender.ID).ClaimedDay < state.Day;
    }

    private static bool ClaimOne(User receiver, User sender)
    {
        if (!CanClaim(receiver, sender)) return false;
        var state = State(receiver);
        History(receiver, sender.ID).ClaimedDay = state.Day;
        state.ReceivedCount++;
        receiver.AddCurrency(CurrencyType.FriendshipPoint, 1);
        return true;
    }

    private static NetWholeUserData Profile(User user, GameContext db) => new()
    {
        Usn = (long)user.ID, Server = 1001, Nickname = user.ManagedNpc?.Name ?? db.Users.Find(user.ID)?.Nickname ?? "Player",
        Lv = user.userPointData.UserLevel, Icon = user.ProfileIconId, IconPrism = user.ProfileIconIsPrism, Frame = user.ProfileFrame,
        TeamCombat = Combat(user), LastActionAt = (user.ManagedNpc?.LastActionUtc ?? user.LastLogin).Ticks,
        GuildName = GuildDirectory.Find(user.Guild.guildId ?? 0)?.Data.Name ?? "", UserTitleId = user.TitleId, UserTitleDisplayData = new()
    };

    private static string? ReceiptKey(string operation, ulong friendId, byte[]? nonce) => nonce is { Length: > 0 }
        ? Convert.ToHexString(SHA256.HashData([.. Encoding.UTF8.GetBytes($"{operation}:{friendId}:"), .. nonce])) : null;

    private static T ExpiredReply<T>() where T : IMessage<T>, new()
    {
        var result = new T();
        switch (result)
        {
            case ResSendFriendshipPoint send: send.Result = FriendshipPointResult.Expired; break;
            case ResObtainFriendshipPoint claim: claim.Result = FriendshipPointResult.Expired; break;
            case ResAllFriendshipPoint all: all.Result = FriendshipPointResult.Expired; break;
            case ResDeleteFriend delete: delete.Result = DeleteFriendResult.FriendException; break;
        }
        return result;
    }

    private static T Change<T>(User user, Func<T> action, string? packetKey = null) where T : IMessage<T>, new()
    {
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (JsonDb.GetUser(user.ID) != user) throw new UnauthorizedAccessException("The account was reloaded.");
            if (packetKey != null && user.Friendships?.Receipts.FirstOrDefault(r => r.Key == packetKey) is {} receipt)
            {
                MissionProgressDelivery.Flush(user);
                var replay = new T();
                replay.MergeFrom(receipt.Response);
                return replay;
            }
            if (packetKey != null && user.Friendships?.ConsumedRequestKeys.Contains(packetKey) == true)
            {
                MissionProgressDelivery.Flush(user);
                return ExpiredReply<T>();
            }
            var owner = ManagedNpcService.Clone(user);
            // Preserve intentionally absent entries instead of the constructor's
            // default stamina currency reappearing during JSON deserialization.
            owner.Currency = new(user.Currency);
            var states = JsonDb.Instance.Users.ToDictionary(u => u, u => u.Friendships == null ? null : ManagedNpcService.Clone(u.Friendships));
            T result;
            try
            {
                using (var scope = new RewardMutationScope(user))
                {
                    result = action();
                    user.PendingMissionTriggers.AddRange(scope.Triggers);
                }
                if (packetKey != null)
                {
                    var state = State(user);
                    state.ConsumedRequestKeys.Add(packetKey);
                    state.Receipts.Insert(0, new FriendRequestReceipt { Key = packetKey, Response = result.ToByteArray() });
                    state.Receipts = state.Receipts.Take(32).ToList();
                }
                JsonDb.Save();
            }
            catch
            {
                foreach (var (participant, state) in states) participant.Friendships = state;
                foreach (var property in typeof(User).GetProperties().Where(p => p.CanRead && p.CanWrite))
                    property.SetValue(user, property.GetValue(owner));
                foreach (var field in typeof(User).GetFields()) field.SetValue(user, field.GetValue(owner));
                throw;
            }
            MissionProgressDelivery.Flush(user);
            return result;
        }
    }
}
