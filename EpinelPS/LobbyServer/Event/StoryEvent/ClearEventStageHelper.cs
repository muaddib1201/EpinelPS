using EpinelPS.Data;
using EpinelPS.Utils;
using Google.Protobuf;

namespace EpinelPS.LobbyServer.Event.StoryEvent;

internal static class ClearEventStageHelper
{
    internal static ResClearEventStage Settle(EventStoryHelper story, int stageId, int count, bool won, bool quick, string? key)
    {
        var state = story.State;
        var user = story.User;
        var history = state.StorySettlements;
        if (key != null && history != null)
        {
            if (key == history.LastKey)
            {
                var replay = ResClearEventStage.Parser.ParseFrom(history.LastResponse);
                UpdateBalances(story, replay);
                return replay;
            }
            if (history.EarlierKeys.Contains(key)) throw EventStoryHelper.Reject("Story battle has already settled.");
        }

        var (battle, dungeon) = story.Resolve(stageId);
        var response = new ResClearEventStage { Reward = new(), BonusReward = new(), RemainTicket = story.Balance };
        if (!won) return response;
        if (quick && (battle.QuickBattleType != QuickBattleType.StageClear ||
            (!state.ClearedStages.Contains(stageId) && state.LastStage != stageId) ||
            (battle.QuickBattleTryMaxCount > 0 && count > battle.QuickBattleTryMaxCount)))
            throw EventStoryHelper.Reject("Quick battle is unavailable for this stage or count.");

        int cost = story.RequireTickets(battle, count);
        story.Spend(cost, response.Reward);
        Grant(user, response.Reward, battle.ClearRewardId, count);
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
        if (key != null)
        {
            history ??= state.StorySettlements = new();
            if (history.LastKey != null) history.EarlierKeys.Add(history.LastKey);
            history.LastKey = key;
            history.LastResponse = response.ToByteArray();
        }
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
