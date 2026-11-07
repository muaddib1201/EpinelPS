using EpinelPS.Data;
using EpinelPS.Database;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace EpinelPS.LobbyServer.Arena;

internal sealed partial class ArenaService
{
    private static long Points(User u) => (long)decimal.Floor((u.Arena?.PointScaled ?? 0) / 10000m);
    private static ArenaRewardRecord? RewardForRank(long rank) => GameData.Instance.ArenaRewards.Values.OrderBy(r => r.MinRank)
        .FirstOrDefault(r => rank >= r.MinRank && (r.MaxRank == 0 || rank <= r.MaxRank));
    private static void AccrueAll(DateTime at)
    {
        foreach (var u in Participants(true))
        {
            var a = u.Arena!;
            long ticks = Math.Max(0, at.Ticks - a.AccruedUtc.Ticks);
            if (ticks == 0) continue;
            long chipTicks = Math.Min(ticks, Math.Max(0, TimeSpan.TicksPerDay - a.BoxTicks));
            if (GameData.Instance.ArenaTimeRewards.Values.FirstOrDefault(r => r.ArenaType == ArenaType.ArenaSpecial && r.ConditionValue == Tier(u, true)) is { } row)
            foreach (var entry in row.TimeStackReward.Where(e => e.TimeSec > 0 && e.ItemValue > 0))
            {
                decimal divisor = (decimal)entry.TimeSec * TimeSpan.TicksPerSecond;
                if (entry.ItemType == RewardType.Currency && entry.ItemId == 5000)
                    a.ChipScaled = Math.Min(Config("ArenaSpecialBoxRewardLimitValue1", 9999) * 10000m, a.ChipScaled + chipTicks * (decimal)entry.ItemValue / divisor);
                else if ((int)entry.ItemType == 9) a.PointScaled += ticks * (decimal)entry.ItemValue / divisor;
            }
            a.BoxTicks += chipTicks;
            a.AccruedUtc = at;
        }
    }
    private void AddCurrencyReward(NetRewardData result, CurrencyType type, long amount)
    {
        if (amount <= 0) return;
        user.Currency[type] = checked(user.Currency.GetValueOrDefault(type) + amount);
        result.Currency.Add(new NetCurrencyData { Type = (int)type, Value = amount, FinalValue = user.Currency[type] });
    }
    private void DailyRewards(DateTime boundary)
    {
        var participants = Participants(false).Where(u => u.Arena!.RookieJoinedUtc < boundary).ToList();
        for (int i = 0; i < participants.Count; i++)
        {
            var u = participants[i];
            if (u.Arena!.RookieJoinedUtc < boundary && RewardForRank(i + 1)?.DailyRewardId is int reward)
                RankMail(u, reward, "新手竞技场每日排名奖励", i + 1, boundary);
        }
    }
    private void SeasonRewards(DateTime boundary)
    {
        var normal = Participants(false).Where(u => u.Arena!.RookieJoinedUtc < boundary).ToList();
        for (int i = 0; i < normal.Count; i++)
            if (normal[i].Arena!.RookieJoinedUtc < boundary && RewardForRank(i + 1)?.SeasonRewardId is int reward)
                RankMail(normal[i], reward, "新手竞技场赛季排名奖励", i + 1, boundary);
        var special = Participants(true).Where(u => u.Arena!.SpecialJoinedUtc < boundary).OrderByDescending(Points).ThenBy(u => u.Arena!.SpecialRank).ThenBy(u => u.ID).ToArray();
        for (int i = 0; i < special.Length; i++)
        {
            var row = GameData.Instance.ConditionRewards.Values.Where(r => r.Group == (SpecialConfig?.ConditionRewardGroup ?? 2))
                .OrderBy(r => r.Priority).FirstOrDefault(r => i + 1 >= r.ValueMin && (r.ValueMax == 0 || i + 1 <= r.ValueMax));
            if (row != null && special[i].Arena!.SpecialJoinedUtc < boundary) RankMail(special[i], row.RewardId, "特殊竞技场赛季排名奖励", i + 1, boundary);
        }
        foreach (var u in JsonDb.Instance.Users.Where(u => u.Arena != null)) { u.Arena!.Rating = 1000; u.Arena.PointScaled = 0; }
    }
    private static void RankMail(User target, int rewardId, string title, int rank, DateTime at)
    {
        if (target.ManagedNpc != null) return;
        var reward = GameData.Instance.RewardDataRecords.GetValueOrDefault(rewardId)
            ?? throw new InvalidDataException("Missing arena rank reward " + rewardId);
        long msn = User.GenerateMsn();
        while (target.MailDatas.ContainsKey(msn)) msn++;
        var mail = new NetUserMailData { Msn = msn, Sender = 1, CreatedAt = at.Ticks, HasReward = true,
            Title = new() { IsPlain = true, Str = title }, Text = new() { IsPlain = true, Str = $"本服竞技场第 {rank} 名的排名奖励。" },
            Nickname = "", State = 1, Type = 1, Period = 90 };
        foreach (var item in reward.Rewards.Where(r => r.RewardType != RewardType.None))
            mail.Items.Add(new NetMailRewardItem { RewardType = (int)item.RewardType, RewardId = item.RewardId, RewardValue = item.RewardValue, ExpiredAt = at.AddDays(90).Ticks });
        target.MailDatas.Add(msn, mail);
    }
    internal ResShowSpecialArenaReward ShowReward()
    {
        Require(true);
        long amount = (long)decimal.Floor(Own.ChipScaled / 10000m);
        return new() { Reward = new NetRewardData { Currency = { new NetCurrencyData { Type = 5000, Value = amount } } }, History = RewardHistory(0).History };
    }
    internal ResObtainSpecialArenaReward ObtainReward(ReqObtainSpecialArenaReward req)
    {
        string? key = Key("special/obtainreward", req);
        if (Replay<ResObtainSpecialArenaReward>(key) is { } receipt) return receipt;
        Require(true);
        var reward = new NetRewardData();
        long amount = (long)decimal.Floor(Own.ChipScaled / 10000m);
        AddCurrencyReward(reward, CurrencyType.ArenaChip, amount);
        if (amount > 0)
        {
            Own.RewardHistory = [new NetSpecialArenaRewardHistoryLog { Seq = ++Own.RewardSequence, Tier = Tier(user, true), SpecialArenaTicket = amount, Point = Points(user),
                AccumulateStartAt = Timestamp.FromDateTime(now.AddTicks(-Own.BoxTicks)), AccumulateEndAt = Timestamp.FromDateTime(now) }.ToByteArray(), .. Own.RewardHistory.Take(49)];
            Own.ChipScaled -= amount * 10000m;
            Own.BoxTicks = 0;
        }
        return Remember(key, new ResObtainSpecialArenaReward { Reward = reward, SpecialArenaPoint = Points(user) });
    }
    internal ResGetSpecialArenaRewardHistory RewardHistory(long lastSeq)
    {
        Require(true);
        return new() { History = new NetSpecialArenaRewardHistory { TotalLogCount = Own.RewardHistory.Count,
            Logs = { Own.RewardHistory.Select(NetSpecialArenaRewardHistoryLog.Parser.ParseFrom).Where(r => lastSeq <= 0 || r.Seq < lastSeq) } } };
    }
    internal ResGetSpecialArenaPointRankList PointRank()
    {
        Require(true);
        var participants = Participants(true).OrderByDescending(Points).ThenBy(u => u.Arena!.SpecialRank).ThenBy(u => u.ID).ToArray();
        return new() { UserPointRank = Array.FindIndex(participants, u => u.ID == user.ID) + 1,
            List = { participants.Take(100).Select((u, i) => new NetSpecialArenaPointData { Rank = i + 1, Point = (int)Math.Min(int.MaxValue, Points(u)), BattleRank = u.Arena!.SpecialRank, User = Profile(u, true) }) } };
    }
}
