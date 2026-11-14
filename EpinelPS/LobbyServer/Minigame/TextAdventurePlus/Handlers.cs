using EpinelPS.Data;
using EpinelPS.Models;
using Google.Protobuf;

namespace EpinelPS.LobbyServer.Minigame.TextAdventurePlus;

public abstract class TextAdventurePlusMessage<TRequest, TResponse> : LobbyMessage
    where TRequest : IMessage, new() where TResponse : IMessage, new()
{
    protected abstract int ManagerId(TRequest request);
    protected virtual bool Saves => true;
    protected abstract TResponse Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, TRequest request);
    protected override async Task HandleAsync()
    {
        var request = await ReadData<TRequest>();
        var user = GetUser();
        var response = TextAdventurePlusService.Execute(user, ManagerId(request), (state, manager) => Process(user, state, manager, request), Saves);
        await WriteDataAsync(response);
    }
}

[GameRequest("/MiniGame/TextAdventurePlus/Enter")]
public class Enter : TextAdventurePlusMessage<ReqEnterMiniGameTextAdventurePlus, ResEnterMiniGameTextAdventurePlus>
{
    protected override int ManagerId(ReqEnterMiniGameTextAdventurePlus req) => req.EventTextAdvPlusManagerTableId;
    protected override ResEnterMiniGameTextAdventurePlus Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqEnterMiniGameTextAdventurePlus req) => TextAdventurePlusService.Enter(state);
}

[GameRequest("/MiniGame/TextAdventurePlus/Enter/Route")]
public class EnterRoute : TextAdventurePlusMessage<ReqEnterMiniGameTextAdventurePlusRoute, ResEnterMiniGameTextAdventurePlusRoute>
{
    protected override int ManagerId(ReqEnterMiniGameTextAdventurePlusRoute req) => req.EventTextAdvPlusManagerTableId;
    protected override ResEnterMiniGameTextAdventurePlusRoute Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqEnterMiniGameTextAdventurePlusRoute req)
    {
        TextAdventurePlusService.OpenRoute(state, manager, req.EventTextAdvPlusRouteTableId);
        return new();
    }
}

[GameRequest("/MiniGame/TextAdventurePlus/Finish")]
public class Finish : TextAdventurePlusMessage<ReqFinishMiniGameTextAdventurePlus, ResFinishMiniGameTextAdventurePlus>
{
    protected override int ManagerId(ReqFinishMiniGameTextAdventurePlus req) => req.EventTextAdvPlusManagerTableId;
    protected override ResFinishMiniGameTextAdventurePlus Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqFinishMiniGameTextAdventurePlus req) => TextAdventurePlusService.Finish(state, manager, req);
}

[GameRequest("/MiniGame/TextAdventurePlus/Reward/Progress")]
public class ClaimProgress : TextAdventurePlusMessage<ReqClaimMiniGameTextAdventurePlusProgressReward, ResClaimMiniGameTextAdventurePlusProgressReward>
{
    protected override int ManagerId(ReqClaimMiniGameTextAdventurePlusProgressReward req) => req.EventTextAdvPlusManagerTableId;
    protected override ResClaimMiniGameTextAdventurePlusProgressReward Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqClaimMiniGameTextAdventurePlusProgressReward req) => TextAdventurePlusService.ClaimProgress(user, state, manager, req.EventTextAdvPlusProgressRewardIdList);
}

[GameRequest("/MiniGame/TextAdventurePlus/Reward/Ending")]
public class ClaimEnding : TextAdventurePlusMessage<ReqClaimMiniGameTextAdventurePlusEndingAlbumReward, ResClaimMiniGameTextAdventurePlusEndingAlbumReward>
{
    protected override int ManagerId(ReqClaimMiniGameTextAdventurePlusEndingAlbumReward req) => req.EventTextAdvPlusManagerTableId;
    protected override ResClaimMiniGameTextAdventurePlusEndingAlbumReward Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqClaimMiniGameTextAdventurePlusEndingAlbumReward req) => TextAdventurePlusService.ClaimEnding(user, state, manager, req.EventTextAdvPlusEventPageTableIdList);
}

[GameRequest("/MiniGame/TextAdventurePlus/Reward/Check")]
public class CheckReward : TextAdventurePlusMessage<ReqCheckRewardMiniGameTextAdventurePlus, ResCheckRewardMiniGameTextAdventurePlus>
{
    protected override int ManagerId(ReqCheckRewardMiniGameTextAdventurePlus req) => req.EventTextAdvPlusManagerTableId;
    protected override bool Saves => false;
    protected override ResCheckRewardMiniGameTextAdventurePlus Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqCheckRewardMiniGameTextAdventurePlus req) => new() { IsRewardExist = TextAdventurePlusService.HasReward(state, manager) };
}

[GameRequest("/MiniGame/TextAdventurePlus/Route/Check")]
public class CheckRoute : TextAdventurePlusMessage<ReqCheckNewRouteMiniGameTextAdventurePlus, ResCheckNewRouteMiniGameTextAdventurePlus>
{
    protected override int ManagerId(ReqCheckNewRouteMiniGameTextAdventurePlus req) => req.EventTextAdvPlusManagerTableId;
    protected override bool Saves => false;
    protected override ResCheckNewRouteMiniGameTextAdventurePlus Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqCheckNewRouteMiniGameTextAdventurePlus req) => new()
    {
        IsNewRouteExist = TextAdventurePlusService.Routes(manager).Any(r => TextAdventurePlusService.IsUnlocked(r, state) && !state.PlayData.BadgeRemovedEventTextAdvPlusRouteTableIdList.Contains(r.Id))
    };
}

[GameRequest("/MiniGame/TextAdventurePlus/Route/Badge/Remove")]
public class RemoveRouteBadge : TextAdventurePlusMessage<ReqRemoveBadgeMiniGameTextAdventurePlusRoute, ResRemoveBadgeMiniGameTextAdventurePlusRoute>
{
    protected override int ManagerId(ReqRemoveBadgeMiniGameTextAdventurePlusRoute req) => req.EventTextAdvPlusManagerTableId;
    protected override ResRemoveBadgeMiniGameTextAdventurePlusRoute Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqRemoveBadgeMiniGameTextAdventurePlusRoute req)
    {
        TextAdventurePlusService.Merge(state.PlayData.BadgeRemovedEventTextAdvPlusRouteTableIdList,
            TextAdventurePlusService.Routes(manager).Where(r => r.Id == req.EventTextAdvPlusRouteTableId && TextAdventurePlusService.IsUnlocked(r, state)).Select(r => r.Id));
        ResRemoveBadgeMiniGameTextAdventurePlusRoute response = new();
        response.BadgeRemovedEventTextAdvPlusRouteTableIdList.AddRange(state.PlayData.BadgeRemovedEventTextAdvPlusRouteTableIdList);
        return response;
    }
}

[GameRequest("/MiniGame/TextAdventurePlus/LootItem/Badge/Remove")]
public class RemoveLootBadge : TextAdventurePlusMessage<ReqRemoveBadgeMiniGameTextAdventurePlusLootItem, ResRemoveBadgeMiniGameTextAdventurePlusLootItem>
{
    protected override int ManagerId(ReqRemoveBadgeMiniGameTextAdventurePlusLootItem req) => req.EventTextAdvPlusManagerTableId;
    protected override ResRemoveBadgeMiniGameTextAdventurePlusLootItem Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqRemoveBadgeMiniGameTextAdventurePlusLootItem req)
    {
        TextAdventurePlusService.Merge(state.PlayData.BadgeRemovedEventTextAdvPlusLootItemTableIdList, req.EventTextAdvPlusLootItemTableIdList.Where(state.PlayData.AcquiredEventTextAdvPlusLootItemTableIdList.Contains));
        ResRemoveBadgeMiniGameTextAdventurePlusLootItem response = new();
        response.BadgeRemovedEventTextAdvPlusLootItemTableIdList.AddRange(state.PlayData.BadgeRemovedEventTextAdvPlusLootItemTableIdList);
        return response;
    }
}

[GameRequest("/MiniGame/TextAdventurePlus/Ending/Badge/Remove")]
public class RemoveEndingBadge : TextAdventurePlusMessage<ReqRemoveBadgeMiniGameTextAdventurePlusEnding, ResRemoveBadgeMiniGameTextAdventurePlusEnding>
{
    protected override int ManagerId(ReqRemoveBadgeMiniGameTextAdventurePlusEnding req) => req.EventTextAdvPlusManagerTableId;
    protected override ResRemoveBadgeMiniGameTextAdventurePlusEnding Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqRemoveBadgeMiniGameTextAdventurePlusEnding req)
    {
        TextAdventurePlusService.Merge(state.PlayData.BadgeRemovedEndingTypeEventTextAdvPlusEventPageTableIdList, req.EventTextAdvPlusEventPageTableIdList.Where(id => state.PlayData.EventTextAdvPlusEventPageTableIdList.Contains(id) && GameData.Instance.EventTextAdvPlusEventPageTable.TryGetValue(id, out var p) && p.ManagerId == manager.Id && p.PageType == EventTextAdvPlusPageType.EndingPage));
        ResRemoveBadgeMiniGameTextAdventurePlusEnding response = new();
        response.BadgeRemovedEndingTypeEventTextAdvPlusEventPageTableIdList.AddRange(state.PlayData.BadgeRemovedEndingTypeEventTextAdvPlusEventPageTableIdList);
        return response;
    }
}

[GameRequest("/MiniGame/TextAdventurePlus/Log/Select")]
public class LogSelect : TextAdventurePlusMessage<ReqLogSelectOptionMiniGameTextAdventurePlus, ResLogSelectOptionMiniGameTextAdventurePlus>
{
    protected override int ManagerId(ReqLogSelectOptionMiniGameTextAdventurePlus req) => req.EventTextAdvPlusManagerTableId;
    protected override bool Saves => false;
    protected override ResLogSelectOptionMiniGameTextAdventurePlus Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqLogSelectOptionMiniGameTextAdventurePlus req) => new();
}

[GameRequest("/MiniGame/TextAdventurePlus/Log/Interrupt")]
public class LogInterrupt : TextAdventurePlusMessage<ReqLogInterruptMiniGameTextAdventurePlus, ResLogInterruptMiniGameTextAdventurePlus>
{
    protected override int ManagerId(ReqLogInterruptMiniGameTextAdventurePlus req) => req.EventTextAdvPlusManagerTableId;
    protected override bool Saves => false;
    protected override ResLogInterruptMiniGameTextAdventurePlus Process(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqLogInterruptMiniGameTextAdventurePlus req) => new();
}
