using System.Globalization;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;
using Google.Protobuf;

namespace EpinelPS.LobbyServer.Event;

internal sealed class ChallengeStage
{
    private readonly User user;
    private readonly EventData state;
    private readonly ChallengeStageData runs;
    private readonly string? packetKey;
    private readonly AutoChargeRecord charge;
    private readonly EventDungeonDifficultRecord[] difficulties;
    private readonly int eventId;
    private readonly int elapsedDays;
    private static GameData Data => GameData.Instance;
    private bool changed;

    public ChallengeStage(User user, int eventId, DateTime now, byte[]? nonce)
    {
        this.user = user;
        this.eventId = eventId;
        packetKey = nonce == null ? null : Convert.ToHexString(nonce);
        if (!Data.eventManagers.TryGetValue(eventId, out var manager) ||
            manager.EventSystemType != EventSystemType.ChallengeModeEvent ||
            !EventHelper.GetLobbyPrivateBannerData(user).Any(b => b.EventId == manager.ParentsEventId))
            throw Reject("Challenge event is unavailable.");

        var story = Data.EventStoryTable.Values.FirstOrDefault(x => x.EventId == eventId);
        if (story == null || !Data.AutoChargeTable.TryGetValue(story.AutoChargeId, out var ticketConfig) ||
            ticketConfig.ItemType != RewardType.Item || ticketConfig.AutoChargeMax < 1 || ticketConfig.AutoChargePlus < 1)
            throw new InvalidDataException($"Missing challenge ticket configuration for event {eventId}.");
        charge = ticketConfig;
        var groups = Data.EventDungeonTable.Values.Where(d => d.Id == story.DungeonId1 || d.Id == story.DungeonId2)
            .Select(d => d.DifficultGroup).ToHashSet();
        difficulties = Data.EventDungeonDifficultTable.Values.Where(d => groups.Contains(d.Group)).ToArray();
        if (difficulties.Length == 0) throw new InvalidDataException($"Missing challenge dungeon for event {eventId}.");

        var start = EventHelper.GetEventStart(eventId, now);
        if (now < start) throw Reject("Challenge event has not started.");
        var today = ResetDate(now);
        elapsedDays = today.DayNumber - ResetDate(start).DayNumber;
        if (!user.EventInfo.TryGetValue(eventId, out var existing))
        {
            existing = new EventData();
            user.EventInfo.Add(eventId, existing);
            changed = true;
        }
        state = existing;
        runs = state.Challenge ??= new();
        if (state.LastStage != 0 && !state.ClearedStages.Contains(state.LastStage) &&
            Data.EventDungeonStageTable.TryGetValue(state.LastStage, out var lastStage) &&
            difficulties.Any(d => d.StageGroup == lastStage.Group))
        {
            // Older saves may contain only the highest stage of this linear challenge.
            state.ClearedStages.AddRange(Data.EventDungeonStageTable.Values
                .Where(s => s.Group == lastStage.Group && s.Step <= lastStage.Step && !state.ClearedStages.Contains(s.Id))
                .Select(s => s.Id));
            changed = true;
        }
        Recharge(today);
    }

    private static BadHttpRequestException Reject(string reason) => new(reason, StatusCodes.Status409Conflict);
    private static DateOnly ResetDate(DateTime time) =>
        DateOnly.FromDateTime(time.AddHours(-JsonDb.Instance.ResetHourUtcTime));

    private IEnumerable<DbItemData> TicketStacks => user.Items.Where(i => i.ItemType == charge.ItemId && i.Count > 0);
    private int Tickets => checked(state.FreeTicket + TicketStacks.Sum(i => i.Count));

    private void Recharge(DateOnly today)
    {
        int date = today.Year * 10000 + today.Month * 100 + today.Day;
        if (state.LastDay >= date) return;
        int days = DateOnly.TryParseExact(state.LastDay.ToString(CultureInfo.InvariantCulture), "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)
            ? today.DayNumber - last.DayNumber : elapsedDays + 1;
        int room = Math.Max(0, charge.AutoChargeMax - Tickets);
        state.FreeTicket += (int)Math.Min(room, (long)days * charge.AutoChargePlus);
        state.LastDay = date;
        changed = true;
    }

    // Packets retried after another battle must not reopen an earlier entry or settle it again.
    private bool Replayed(string? key) => key != null && runs.RecentRequests.Contains(key);

    private void Remember(string? key)
    {
        if (key == null) return;
        if (runs.RecentRequests.Count >= 128) runs.RecentRequests.RemoveAt(0);
        runs.RecentRequests.Add(key);
    }

    private string? BattleRequestKey(string operation, int stage, int team, long? clientTime) =>
        clientTime > 0 ? $"{operation}/{stage}/{team}/{clientTime}" : packetKey;

    public ResChallengeEventStageData Get()
    {
        const int teamType = (int)TeamType.ChallengeModeEvent;
        var team = user.UserTeams.GetValueOrDefault(teamType) ?? user.UserTeams.GetValueOrDefault((int)TeamType.StoryEvent);
        var response = new ResChallengeEventStageData { RemainTicket = Tickets, TeamData = team?.Clone() ?? new() };
        response.TeamData.Type = teamType;
        var cleared = state.ClearedStages.Append(state.LastStage).Distinct()
            .Select(id => Data.EventDungeonStageTable.GetValueOrDefault(id)).OfType<EventDungeonStageRecord>();
        foreach (var difficulty in difficulties)
        {
            var last = cleared.Where(s => s.Group == difficulty.StageGroup).MaxBy(s => s.Step);
            if (last != null) response.LastClearedEventStageList.Add(new NetLastClearedEventStageData { DifficultyId = difficulty.Id, StageId = last.Id });
        }
        return response;
    }

    private bool Cleared(int id) => state.ClearedStages.Contains(id) || state.LastStage == id;

    private bool MeetsCondition(EventDungeonOpenCondition type, int value) => type switch
    {
        EventDungeonOpenCondition.None => true,
        EventDungeonOpenCondition.EventDungeonStageClear => Cleared(value),
        EventDungeonOpenCondition.EventDay => elapsedDays >= value,
        _ => false
    };

    private (EventDungeonDifficultRecord Difficulty, EventDungeonSpotBattleRecord Battle) Resolve(int stageId)
    {
        if (!Data.EventDungeonStageTable.TryGetValue(stageId, out var stage) ||
            stage.StageContentsType != EventDungeonContentsType.SpotBattle ||
            !Data.EventDungeonSpotBattleTable.TryGetValue(stage.StageId, out var battle))
            throw Reject("Unknown challenge stage.");
        var difficulty = difficulties.FirstOrDefault(d => d.StageGroup == stage.Group);
        if (difficulty == null || !MeetsCondition(difficulty.OpenConditionType, difficulty.OpenConditionValue) ||
            !battle.OpenConditionList.All(c => MeetsCondition(c.OpenConditionType, c.OpenConditionValue)))
            throw Reject("Challenge stage is locked or belongs to another event.");
        if (!stage.IsRepeatClear && Cleared(stageId)) throw Reject("Challenge stage cannot be repeated.");
        if (battle.TicketItemType != charge.ItemType || battle.TicketItemId != charge.ItemId || battle.TicketItemCount <= 0)
            throw new InvalidDataException($"Invalid ticket cost for challenge stage {stageId}.");
        return (difficulty, battle);
    }

    private void RequireTickets(EventDungeonSpotBattleRecord battle, int count)
    {
        if (count <= 0 || (long)battle.TicketItemCount * count > Tickets)
            throw Reject("Insufficient challenge tickets or invalid clear count.");
    }

    public ResEnterChallengeEventStage Enter(ReqEnterChallengeEventStage req)
    {
        var key = BattleRequestKey("enter", req.StageId, req.TeamNumber, req.AntiCheatAdditionalInfo?.ClientLocalTime);
        if (Replayed(key)) return new();
        var (_, battle) = Resolve(req.StageId);
        RequireTickets(battle, 1);
        // The enter acknowledgement has no fields. Only a matching victory spends the ticket.
        runs.Battle = new ChallengeBattle { StageId = req.StageId, TeamNumber = req.TeamNumber };
        Remember(key);
        changed = true;
        return new();
    }

    public ResClearChallengeEventStage Clear(ReqClearChallengeEventStage req)
    {
        var key = BattleRequestKey("clear", req.StageId, req.TeamNumber, req.AntiCheatAdditionalInfo?.ClientLocalTime);
        var entered = runs.Battle;
        if (entered == null || entered.StageId != req.StageId || entered.TeamNumber != req.TeamNumber)
            throw Reject("No matching challenge battle was entered.");
        if (entered.Response != null)
        {
            if (entered.Result != req.BattleResult || (Replayed(key) && key != entered.ResultRequestKey))
                throw Reject("Challenge battle has already ended.");
            var previous = ResClearChallengeEventStage.Parser.ParseFrom(entered.Response);
            previous.RemainTicket = Tickets;
            if (previous.Reward != null) UpdateFinalBalances(previous.Reward);
            if (previous.FirstClearReward != null) UpdateFinalBalances(previous.FirstClearReward);
            return previous;
        }
        if (Replayed(key)) throw Reject("This challenge result belongs to an earlier battle.");
        var (difficulty, battle) = Resolve(req.StageId);
        var response = new ResClearChallengeEventStage { Reward = new NetRewardData() };
        if (req.BattleResult == 1)
        {
            RequireTickets(battle, 1);
            bool first = !Cleared(req.StageId);
            var regular = RewardTable(battle.ClearRewardId);
            var bonus = first ? RewardTable(battle.FirstClearRewardId) : null;
            response.Reward = Grant(regular, 1);
            if (bonus != null) response.FirstClearReward = Grant(bonus, 1);
            Spend(battle.TicketItemCount, response.Reward);
            if (first)
            {
                state.ClearedStages.Add(req.StageId);
                state.LastStage = req.StageId;
                state.Diff = difficulty.Id;
            }
            // Both reward messages carry absolute balances, even when their currencies or items overlap.
            UpdateFinalBalances(response.Reward);
            if (response.FirstClearReward != null) UpdateFinalBalances(response.FirstClearReward);
            QueueClearTriggers(req.StageId, 1);
        }
        response.RemainTicket = Tickets;
        entered.Result = req.BattleResult;
        entered.ResultRequestKey = key;
        entered.Response = response.ToByteArray();
        Remember(key);
        changed = true;
        return response;
    }

    public ResFastClearChallengeEventStage FastClear(ReqFastClearChallengeEventStage req)
    {
        if (packetKey != null && packetKey == runs.LastQuickRequest && runs.LastQuickResponse != null)
        {
            var previous = ResFastClearChallengeEventStage.Parser.ParseFrom(runs.LastQuickResponse);
            previous.RemainTicket = Tickets;
            UpdateFinalBalances(previous.Reward);
            return previous;
        }
        if (Replayed(packetKey)) throw Reject("This quick battle has already been settled.");
        var (_, battle) = Resolve(req.StageId);
        if (!Cleared(req.StageId) || battle.QuickBattleType != QuickBattleType.StageClear ||
            (battle.QuickBattleTryMaxCount > 0 && req.ClearCount > battle.QuickBattleTryMaxCount))
            throw Reject("Quick battle is unavailable for this stage or clear count.");
        RequireTickets(battle, req.ClearCount);
        var reward = Grant(RewardTable(battle.ClearRewardId), req.ClearCount);
        Spend(battle.TicketItemCount * req.ClearCount, reward);
        var response = new ResFastClearChallengeEventStage { Reward = reward, RemainTicket = Tickets };
        runs.Battle = null;
        runs.LastQuickRequest = packetKey;
        runs.LastQuickResponse = packetKey == null ? null : response.ToByteArray();
        QueueClearTriggers(req.StageId, req.ClearCount);
        Remember(packetKey);
        changed = true;
        return response;
    }

    private static RewardRecord? RewardTable(int id) => id == 0 ? null :
        Data.GetRewardTableEntry(id) ?? throw new InvalidDataException($"Missing challenge reward {id}.");

    private NetRewardData Grant(RewardRecord? table, int count)
    {
        if (table == null) return new();
        if (count == 1) return RewardUtils.RegisterRewardsForUser(user, table);
        // Scale all configured amounts before modifying a balance.
        var scaled = new RewardRecord { Id = table.Id, UserExp = checked(table.UserExp * count),
            Rewards = table.Rewards.Select(r => new Reward_Data {
                RewardId = r.RewardId, RewardType = r.RewardType, RewardPercent = r.RewardPercent,
                RewardValue = checked(r.RewardValue * count)
            }).ToArray() };
        return RewardUtils.RegisterRewardsForUser(user, scaled);
    }

    private void Spend(int cost, NetRewardData reward)
    {
        int free = Math.Min(cost, state.FreeTicket);
        state.FreeTicket -= free;
        cost -= free;
        foreach (var stack in TicketStacks)
        {
            if (cost == 0) break;
            int spent = Math.Min(cost, stack.Count);
            stack.Count -= spent;
            cost -= spent;
            reward.UserItems.Add(NetUtils.ToNet(stack));
        }
    }

    private void UpdateFinalBalances(NetRewardData reward)
    {
        foreach (var currency in reward.Currency) currency.FinalValue = user.Currency.GetValueOrDefault((CurrencyType)currency.Type);
        foreach (var item in reward.UserItems) item.Count = user.Items.FirstOrDefault(i => i.Isn == item.Isn)?.Count ?? 0;
    }

    private void QueueClearTriggers(int stageId, int count) =>
        runs.PendingClear = new() { StageId = stageId, Count = count, CreatedAt = DateTime.UtcNow.AddHours(9).Ticks };

    // The JSON reward and its two SQLite mission triggers span separate stores. Keep the
    // pending notification with the reward until both triggers have been written together.
    public void Save()
    {
        var pending = runs.PendingClear;
        if (changed || pending != null) JsonDb.Save();
        changed = false;
        if (pending == null) return;
        using var db = GameContext.CreateNew();
        var triggers = db.Triggers.Where(t => t.UserId == user.ID && t.CreatedAt == pending.CreatedAt &&
            ((t.Type == Trigger.EventStageClear && t.ConditionId == pending.StageId) ||
             (t.Type == Trigger.EventDungeonStageClear && t.ConditionId == eventId))).ToList();
        if (triggers.Count == 0)
        {
            triggers = [
                new() { UserId = user.ID, CreatedAt = pending.CreatedAt, Value = pending.Count,
                    Type = Trigger.EventStageClear, ConditionId = pending.StageId },
                new() { UserId = user.ID, CreatedAt = pending.CreatedAt, Value = pending.Count,
                    Type = Trigger.EventDungeonStageClear, ConditionId = eventId }
            ];
            db.Triggers.AddRange(triggers);
            db.SaveChanges();
        }
        foreach (var trigger in triggers) MessengerMessageCreator.OnTriggerAdded(user, trigger);
        runs.PendingClear = null;
        JsonDb.Save();
    }
}
