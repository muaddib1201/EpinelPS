using System.Security.Cryptography;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;
using Google.Protobuf;

namespace EpinelPS.LobbyServer.Arena;

internal sealed partial class ArenaService
{
    internal ResArenaStart Start(ReqArenaStart req)
    {
        string key = BattleKey("start", req);
        if (Replay<ResArenaStart>(key) is { } receipt) return receipt;
        var battle = Begin(req.EnemyUsn, false);
        return Remember(key, new ResArenaStart { Enemy = NetArenaEnemyData.Parser.ParseFrom(battle.Enemy), Nonce = battle.Nonce });
    }
    internal ResSpecialArenaStart SpecialStart(ReqSpecialArenaStart req)
    {
        string key = BattleKey("special/start", req);
        if (Replay<ResSpecialArenaStart>(key) is { } receipt) return receipt;
        var battle = Begin(req.EnemyUsn, true);
        return Remember(key, new ResSpecialArenaStart { Enemy = NetArenaEnemyData.Parser.ParseFrom(battle.Enemy), Nonce = battle.Nonce,
            SimulationTarget = SpecialArenaSimulationTarget.Client });
    }
    private ArenaBattle Begin(long id, bool special)
    {
        var enemy = Enemy(id, special);
        if (!ArenaTeams.Ensure(user, special, true)) throw new BadHttpRequestException("Configure a valid arena attack team first.", 400);
        if (Own.Pending is { } current && current.Season == World.Season && now - current.StartedUtc < TimeSpan.FromMinutes(15))
        {
            if (current.Special != special || current.EnemyId != (ulong)id) throw new BadHttpRequestException("Another arena battle is in progress.", 409);
            return current;
        }
        var snapshot = ArenaTeams.Enemy(enemy, special);
        int count = special ? Own.SpecialCount : Own.RookieCount;
        // The current rookie UI grants five free battles; the bundled legacy ArenaTodayMaxCount still says three.
        int free = special ? SpecialConfig?.FreePlayCount ?? 2 : 5;
        var ticket = (CurrencyType)(special ? SpecialConfig?.TicketId ?? 6002 : 6001);
        int cost = special ? Math.Max(1, SpecialConfig?.TicketValue ?? 1) : 1;
        if (count >= free)
        {
            if (user.Currency.GetValueOrDefault(ticket) < cost) throw new BadHttpRequestException("No free arena attempts or tickets remain.", 400);
            user.Currency[ticket] -= cost;
        }
        if (special) Own.SpecialCount++; else Own.RookieCount++;
        Own.Pending = new ArenaBattle { MatchId = Guid.NewGuid().ToString("N"), EnemyId = enemy.ID, Special = special, Season = World.Season,
            StartedUtc = now, Nonce = RandomNumberGenerator.GetInt32(1, int.MaxValue), Enemy = snapshot.ToByteArray(),
            AttackTeams = user.UserTeams[ArenaTeams.Type(special, true)].Teams.Select(t => t.TeamNumber).ToList() };
        if (!special) QueueTrigger(Trigger.RookieArenaPlayCount, 1, 0);
        return Own.Pending;
    }
    private (ArenaBattle Battle, User Enemy) Pending(long id, bool special)
    {
        Require(special);
        var battle = Own.Pending;
        if (id <= 0 || battle == null || battle.Special != special || battle.EnemyId != (ulong)id || battle.Season != World.Season ||
            now < battle.StartedUtc || now - battle.StartedUtc >= TimeSpan.FromMinutes(15))
            throw new BadHttpRequestException("No matching active arena battle.", 409);
        var enemy = JsonDb.GetUser(battle.EnemyId);
        if (enemy?.Arena == null || enemy.IsBanned) throw new BadHttpRequestException("Arena opponent is no longer available.", 409);
        return (battle, enemy);
    }
    internal ResArenaResult Result(ReqArenaResult req)
    {
        string key = BattleKey("result", req);
        if (Replay<ResArenaResult>(key) is { } receipt) return receipt;
        return Remember(key, FinishRookie(req));
    }
    private ResArenaResult FinishRookie(ReqArenaResult req)
    {
        var (battle, enemy) = Pending(req.EnemyUsn, false);
        if (!battle.AttackTeams.Contains(req.TeamNumber)) throw new BadHttpRequestException("Invalid arena attack team number.", 400);
        var result = new NetArenaResult { User = new NetArenaRankData { BeforeRank = Rank(user, false), BeforeRating = Own.Rating },
            Enemy = new NetArenaRankData { BeforeRank = Rank(enemy, false), BeforeRating = enemy.Arena!.Rating } };
        int transfer = req.IsWin ? RookieRatingTransfer(Own.Rating, enemy.Arena!.Rating)
            : RookieRatingTransfer(enemy.Arena!.Rating, Own.Rating);
        int delta = req.IsWin ? transfer : -transfer;
        Own.Rating = (int)Math.Clamp((long)Own.Rating + delta, 0L, int.MaxValue);
        enemy.Arena!.Rating = (int)Math.Clamp((long)enemy.Arena.Rating - delta, 0L, int.MaxValue);
        result.User.AfterRank = Rank(user, false); result.User.AfterRating = Own.Rating;
        result.Enemy.AfterRank = Rank(enemy, false); result.Enemy.AfterRating = enemy.Arena.Rating;
        var reward = new NetRewardData();
        if (req.IsWin)
        {
            long value = RewardForRank(result.User.AfterRank)?.WinRewardValue ?? 138;
            AddCurrencyReward(reward, CurrencyType.ArenaChip, value);
            QueueTrigger(Trigger.WinArena, 1, 0);
        }
        var attack = new NetRookieArenaRecordData { User = Profile(enemy, false), ArenaAt = now.Ticks,
            BeforeRating = result.User.BeforeRating, AfterRating = result.User.AfterRating, IsWin = req.IsWin, IsAttack = true, Season = World.Season, MatchId = battle.MatchId };
        var defense = new NetRookieArenaRecordData { User = Profile(user, false), ArenaAt = now.Ticks,
            BeforeRating = result.Enemy.BeforeRating, AfterRating = result.Enemy.AfterRating, IsWin = !req.IsWin, IsAttack = false, Season = World.Season, MatchId = battle.MatchId };
        Own.RookieRecords = [attack.ToByteArray(), .. Own.RookieRecords.Take(49)];
        enemy.Arena.RookieRecords = [defense.ToByteArray(), .. enemy.Arena.RookieRecords.Take(49)];
        Own.Pending = null;
        return new ResArenaResult { ArenaCount = Own.RookieCount, ArenaResult = result, Reward = reward,
            Currency = new NetUserCurrencyData { Type = 5000, Value = user.Currency.GetValueOrDefault(CurrencyType.ArenaChip) }, User = Profile(user, false), Enemy = Profile(enemy, false) };
    }
    private static int RookieRatingTransfer(int winnerRating, int loserRating)
    {
        // Elo approximation: official coefficients/rounding are unverified. K=40 keeps
        // the existing equal-rating +/-20 baseline; scale=400 and bounds are local choices.
        const int maximumChange = 40;
        const double ratingScale = 400;
        double gain = maximumChange / (1 + Math.Pow(10, ((double)winnerRating - loserRating) / ratingScale));
        return Math.Clamp((int)Math.Round(gain, MidpointRounding.AwayFromZero), 1, maximumChange);
    }
    internal ResSpecialArenaResult SpecialResult(ReqSpecialArenaResult req)
    {
        string key = BattleKey("special/result", req);
        if (Replay<ResSpecialArenaResult>(key) is { } receipt) return receipt;
        return Remember(key, FinishSpecial(req));
    }
    private ResSpecialArenaResult FinishSpecial(ReqSpecialArenaResult req)
    {
        var (battle, enemy) = Pending(req.EnemyUsn, true);
        var rounds = req.Result.OrderBy(r => r.Round).ToArray();
        if (rounds.Length is < 2 or > 3 || !rounds.Select(r => r.Round).SequenceEqual(Enumerable.Range(1, rounds.Length)) ||
            (rounds.Length == 2 && rounds[0].IsWin != rounds[1].IsWin))
            throw new BadHttpRequestException("Special arena requires distinct completed best-of-three rounds.", 400);
        bool won = rounds.Count(r => r.IsWin) >= 2;
        var beforeUser = new NetSpecialArenaRankData { Rank = Own.SpecialRank, Tier = Tier(user, true) };
        var beforeEnemy = new NetSpecialArenaRankData { Rank = enemy.Arena!.SpecialRank, Tier = Tier(enemy, true) };
        // Advance already accrued both players at their old tier up to now.
        if (won && Own.SpecialRank > enemy.Arena.SpecialRank)
            (Own.SpecialRank, enemy.Arena.SpecialRank) = (enemy.Arena.SpecialRank, Own.SpecialRank);
        var result = new NetSpecialArenaResult { User = new NetSpecialArenaRankResultData { Before = beforeUser,
            After = new NetSpecialArenaRankData { Rank = Own.SpecialRank, Tier = Tier(user, true) } },
            Enemy = new NetSpecialArenaRankResultData { Before = beforeEnemy,
                After = new NetSpecialArenaRankData { Rank = enemy.Arena.SpecialRank, Tier = Tier(enemy, true) } } };
        int limit = Math.Clamp(SpecialConfig?.RecordMaxCount ?? 50, 1, 100);
        Own.SpecialRecords = [new NetSpecialArenaRecordData { User = Profile(enemy, true), ArenaAt = now.Ticks, BeforeRank = beforeUser.Rank,
            AfterRank = Own.SpecialRank, IsWin = won, IsAttack = true, Season = World.Season, MatchId = battle.MatchId }.ToByteArray(), .. Own.SpecialRecords.Take(limit - 1)];
        enemy.Arena.SpecialRecords = [new NetSpecialArenaRecordData { User = Profile(user, true), ArenaAt = now.Ticks, BeforeRank = beforeEnemy.Rank,
            AfterRank = enemy.Arena.SpecialRank, IsWin = !won, IsAttack = false, Season = World.Season, MatchId = battle.MatchId }.ToByteArray(), .. enemy.Arena.SpecialRecords.Take(limit - 1)];
        if (won) QueueTrigger(Trigger.WinArena, 1, 0);
        QueueTrigger(Trigger.SpecialArenaTier, result.User.After.Tier, 0);
        Own.Pending = null;
        return new ResSpecialArenaResult { SpecialArenaCount = Own.SpecialCount, Battle = result, Reward = new(),
            User = Profile(user, true), Enemy = Profile(enemy, true), Currencies = { user.Currency.Select(c => new NetUserCurrencyData { Type = (int)c.Key, Value = c.Value }) } };
    }

    private string BattleKey(string route, IMessage req) => Key(route, req)
        ?? throw new BadHttpRequestException("Arena battles require a transport nonce to identify retries.", 400);

    private void QueueTrigger(Trigger type, int value, int condition)
    {
        if (user.ManagedNpc != null && RewardMutationScope.DefersSave)
        { user.AddTrigger(type, value, condition); return; }
        long at = Math.Max(now.AddHours(9).Ticks, Own.LastTriggerAt + 1);
        Own.LastTriggerAt = at;
        Own.PendingTriggers.Add(new TriggerModelNew { UserId = user.ID, CreatedAt = at, Type = type, ConditionId = condition, Value = value });
    }
    internal void FlushTriggers()
    {
        if (user.Arena?.PendingTriggers.Count is not > 0) return;
        var pending = Own.PendingTriggers;
        using var db = GameContext.CreateNew();
        foreach (var t in pending)
        {
            if (!db.Triggers.Any(x => x.UserId == user.ID && x.Type == t.Type && x.CreatedAt == t.CreatedAt && x.ConditionId == t.ConditionId))
                db.Triggers.Add(new TriggerModelNew { UserId = t.UserId, Type = t.Type, CreatedAt = t.CreatedAt, ConditionId = t.ConditionId, Value = t.Value });
        }
        db.SaveChanges();
        foreach (var t in pending) MessengerMessageCreator.OnTriggerAdded(user, t);
        Own.PendingTriggers = [];
        try { JsonDb.Save(); } catch { Own.PendingTriggers = pending; throw; }
    }
}
