using EpinelPS.Data;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Event.StoryEvent;

internal sealed class EventStoryHelper
{
    private readonly EventStoryRecord? story;
    private readonly AutoChargeRecord? charge;
    private static GameData Tables => GameData.Instance;
    internal User User { get; }
    internal EventData State { get; }
    internal int EventId { get; }

    internal EventStoryHelper(User user, int eventId)
    {
        User = user;
        EventId = eventId;
        if (!Tables.eventManagers.TryGetValue(eventId, out var manager) || manager.EventSystemType != EventSystemType.StoryEvent)
            throw Reject("Unknown story event.");
        story = Tables.EventStoryTable.Values.FirstOrDefault(s => s.EventId == eventId);
        charge = story == null ? null : Tables.AutoChargeTable.GetValueOrDefault(story.AutoChargeId);
        if (!user.EventInfo.TryGetValue(eventId, out var state))
            user.EventInfo[eventId] = state = new EventData();
        State = state;

        int maxFree = charge is { AutoChargeMax: > 0 } ? charge.AutoChargeMax : 5;
        int today = user.GetDateDay();
        if (state.LastDay < today)
        {
            state.FreeTicket = maxFree;
            state.LastDay = today;
        }
        else state.FreeTicket = Math.Clamp(state.FreeTicket, 0, maxFree);
    }

    private IEnumerable<DbItemData> TicketStacks =>
        User.Items.Where(item => item.ItemType == charge?.ItemId && item.Count > 0);

    internal int Balance => checked(State.FreeTicket + TicketStacks.Sum(stack => stack.Count));

    internal (EventDungeonSpotBattleRecord? Battle, EventDungeonRecord Dungeon) Resolve(int stageId)
    {
        if (story == null || !Tables.EventDungeonStageTable.TryGetValue(stageId, out var stage))
            throw Reject("Unknown story battle.");
        var dungeon = Tables.EventDungeonTable.Values.FirstOrDefault(d =>
            (d.Id == story.DungeonId1 || d.Id == story.DungeonId2) &&
            Tables.EventDungeonDifficultTable.Values.Any(level => level.Group == d.DifficultGroup && level.StageGroup == stage.Group));
        if (dungeon == null) throw Reject("Story stage does not belong to this event.");
        // Embedded story minigames use the dungeon's one-ticket entry and bonus without a spot-battle row.
        if (stage.StageContentsType != EventDungeonContentsType.SpotBattle) return (null, dungeon);
        if (!Tables.EventDungeonSpotBattleTable.TryGetValue(stage.StageId, out var battle) ||
            charge == null || charge.ItemType != RewardType.Item || battle.TicketItemType != charge.ItemType ||
            battle.TicketItemId != charge.ItemId || battle.TicketItemCount <= 0)
            throw new InvalidDataException($"Invalid story stage ticket configuration for {stageId}.");
        return (battle, dungeon);
    }

    internal int RequireTickets(EventDungeonSpotBattleRecord? battle, int count)
    {
        long cost = (long)(battle?.TicketItemCount ?? 1) * count;
        if (count <= 0 || cost > Balance) throw Reject("Insufficient story tickets or invalid clear count.");
        return (int)cost;
    }

    internal void Spend(int cost, NetRewardData reward)
    {
        int free = Math.Min(State.FreeTicket, cost);
        State.FreeTicket -= free;
        int remaining = cost - free;
        foreach (var stack in TicketStacks)
        {
            if (remaining == 0) break;
            int spent = Math.Min(stack.Count, remaining);
            stack.Count -= spent;
            remaining -= spent;
            reward.UserItems.Add(NetUtils.ToNet(stack));
        }
    }

    internal ResEnterEventStage Enter(int stageId, int team, string? key)
    {
        var history = State.StorySettlements ??= new();
        if (key != null && history.ProcessedKeys.Contains(key)) return new();
        var (battle, _) = Resolve(stageId);
        RequireTickets(battle, 1);
        if (history.Battle != null) history.ProcessedKeys.UnionWith(history.Battle.ResultKeys);
        history.Battle = new StoryStageBattle { StageId = stageId, TeamNumber = team };
        if (key != null) history.ProcessedKeys.Add(key);
        return new();
    }

    internal static BadHttpRequestException Reject(string reason) => new(reason, StatusCodes.Status409Conflict);
}
