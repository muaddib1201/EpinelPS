using EpinelPS.Data;
using EpinelPS.Utils;
using Google.Protobuf;

namespace EpinelPS.LobbyServer.Event.StoryEvent;

internal static class ClearEventStageHelper
{
    internal static ResClearEventStage Clear(EventStoryHelper story, ReqClearEventStage req, string? key)
    {
        var history = story.State.StorySettlements;
        if (history?.Battle is not { } entry || entry.StageId != req.StageId || entry.TeamNumber != req.TeamNumber)
            throw EventStoryHelper.Reject("No matching story battle was entered.");
        if (key != null && history.ProcessedKeys.Contains(key)) throw EventStoryHelper.Reject("Story result belongs to an earlier battle.");
        ResClearEventStage response;
        if (entry.Response != null)
        {
            if (entry.Result != req.BattleResult) throw EventStoryHelper.Reject("Story battle has already settled.");
            response = Replay(story, entry.Response);
        }
        else
        {
            response = Settle(story, req.StageId, 1, req.BattleResult == 1, quick: false);
            entry.Result = req.BattleResult;
            entry.Response = response.ToByteArray();
        }
        if (key != null) entry.ResultKeys.Add(key);
        return response;
    }

    internal static ResFastClearEventStage FastClear(EventStoryHelper story, ReqFastClearEventStage req, string? key)
    {
        var history = story.State.StorySettlements ??= new();
        ResClearEventStage response;
        if (key != null && key == history.LastSweepKey && history.LastSweepResponse != null)
            response = Replay(story, history.LastSweepResponse);
        else
        {
            if (key != null && history.ProcessedKeys.Contains(key)) throw EventStoryHelper.Reject("Sweep has already settled.");
            response = Settle(story, req.StageId, req.ClearCount, won: true, quick: true);
            if (key != null)
            {
                history.ProcessedKeys.Add(key);
                history.LastSweepKey = key;
                history.LastSweepResponse = response.ToByteArray();
            }
        }
        return new ResFastClearEventStage { RemainTicket = response.RemainTicket, Reward = response.Reward, BonusReward = response.BonusReward };
    }

    private static ResClearEventStage Replay(EventStoryHelper story, byte[] saved)
    {
        var response = ResClearEventStage.Parser.ParseFrom(saved);
        UpdateBalances(story, response);
        return response;
    }

    private static ResClearEventStage Settle(EventStoryHelper story, int stageId, int count, bool won, bool quick)
    {
        var state = story.State;
        var user = story.User;
        var (battle, dungeon) = story.Resolve(stageId);
        var response = new ResClearEventStage { Reward = new(), BonusReward = new(), RemainTicket = story.Balance };
        if (!won) return response;
        if (quick && battle is { QuickBattleTryMaxCount: > 0 } && count > battle.QuickBattleTryMaxCount)
            throw EventStoryHelper.Reject("Sweep count exceeds the stage limit.");

        int cost = story.RequireTickets(battle, count);
        story.Spend(cost, response.Reward);
        Grant(user, response.Reward, battle?.ClearRewardId ?? 0, count);
        Grant(user, response.BonusReward, dungeon.BonusRewardId, count);
        if (!quick)
        {
            if (!state.ClearedStages.Contains(stageId)) state.ClearedStages.Add(stageId);
            state.LastStage = Math.Max(state.LastStage, stageId);
            state.Diff = 0;
        }
        user.AddTrigger(Trigger.EventStageClear, count, stageId);
        user.AddTrigger(Trigger.EventDungeonStageClear, count, story.EventId);
        foreach (var item in response.BonusReward.Item)
            user.AddTrigger(Trigger.ObtainEventCurrencyMaterial, item.Count, item.Tid);

        UpdateBalances(story, response);
        return response;
    }

    private static void Grant(User user, NetRewardData reward, int rewardId, int count)
    {
        if (rewardId == 0) return;
        var record = GameData.Instance.GetRewardTableEntry(rewardId) ??
            throw new InvalidDataException($"Missing story stage reward {rewardId}.");
        foreach (var item in record.Rewards)
        {
            if (item != null && item.RewardType != RewardType.None)
                RewardUtils.AddSingleObject(user, ref reward, item.RewardId, item.RewardType, checked(item.RewardValue * count));
        }
    }

    private static void UpdateBalances(EventStoryHelper story, ResClearEventStage response)
    {
        response.RemainTicket = story.Balance;
        foreach (var reward in new[] { response.Reward, response.BonusReward })
        {
            foreach (var currency in reward.Currency)
                currency.FinalValue = story.User.GetCurrencyVal((CurrencyType)currency.Type);
            foreach (var stack in reward.UserItems)
                stack.Count = story.User.Items.FirstOrDefault(i => i.Isn == stack.Isn)?.Count ?? 0;
        }
    }
}
