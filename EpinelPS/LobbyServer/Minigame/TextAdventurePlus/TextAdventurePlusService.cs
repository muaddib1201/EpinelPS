using System.Globalization;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Utils;
using Google.Protobuf.Collections;
using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.Minigame.TextAdventurePlus;

internal static class TextAdventurePlusService
{
    private static GameData Tables => GameData.Instance;

    internal static T Execute<T>(User user, int managerId,
        Func<TextAdventurePlusData, EventTextAdvPlusManagerRecord_Raw, T> action, bool save = true)
    {
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!ReferenceEquals(JsonDb.GetUser(user.ID), user)) throw new BadHttpRequestException("Stale session", 409);
            Require(Tables.EventTextAdvPlusManagerTable.TryGetValue(managerId, out var manager), "Unknown text adventure manager");
            user.TextAdventurePlusDatas.TryGetValue(managerId, out var state);
            state ??= new();
            // Keep failed validation/save attempts from changing the in-memory save.
            var before = save ? JsonConvert.SerializeObject(user) : null;
            try
            {
                var response = action(state, manager!);
                if (save)
                {
                    user.TextAdventurePlusDatas[managerId] = state;
                    JsonDb.Save();
                }
                return response;
            }
            catch
            {
                if (before != null)
                {
                    var restored = JsonConvert.DeserializeObject<User>(before)!;
                    foreach (var p in typeof(User).GetProperties().Where(p => p.CanRead && p.CanWrite)) p.SetValue(user, p.GetValue(restored));
                    foreach (var f in typeof(User).GetFields()) f.SetValue(user, f.GetValue(restored));
                }
                throw;
            }
        }
    }

    internal static ResEnterMiniGameTextAdventurePlus Enter(TextAdventurePlusData state)
    {
        ResEnterMiniGameTextAdventurePlus response = new() { PlayData = state.PlayData.Clone() };
        response.ReceivedEventTextAdvPlusProgressRewardIdList.AddRange(state.ReceivedProgressRewards);
        response.ReceivedEventTextAdvPlusEventPageTableIdList.AddRange(state.ReceivedEndingRewards);
        return response;
    }

    internal static IEnumerable<EventTextAdvPlusRouteRecord_Raw> Routes(EventTextAdvPlusManagerRecord_Raw manager) =>
        Tables.EventTextAdvPlusRouteTable.Values.Where(r => r.ManagerId == manager.Id && r.RouteGroupId == manager.RouteSelectGroupId);

    internal static bool IsUnlocked(EventTextAdvPlusRouteRecord_Raw route, TextAdventurePlusData state) => route.RouteUnlockType switch
    {
        EventTextAdventurePlusRouteUnlockType.None => true,
        EventTextAdventurePlusRouteUnlockType.Date => DateTime.TryParse(route.RouteUnlockValue, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) && DateTime.UtcNow >= at,
        EventTextAdventurePlusRouteUnlockType.Item => int.TryParse(route.RouteUnlockValue, out var id) && state.PlayData.AcquiredEventTextAdvPlusLootItemTableIdList.Contains(id),
        _ => false
    };

    internal static void OpenRoute(TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, int routeId)
    {
        var route = Routes(manager).FirstOrDefault(r => r.Id == routeId);
        Require(route != null && IsUnlocked(route, state), "Unknown or locked text adventure route");
        state.ActiveRouteId = routeId;
        state.PlayData.LastEnteredEventTextAdvPlusRouteTableId = routeId;
    }

    internal static ResFinishMiniGameTextAdventurePlus Finish(TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, ReqFinishMiniGameTextAdventurePlus req)
    {
        Require(Routes(manager).Any(r => r.Id == req.EventTextAdvPlusRouteTableId), "Unknown text adventure route");
        Require(req.EventTextAdvPlusEventPageTableIdList.All(id => Tables.EventTextAdvPlusEventPageTable.TryGetValue(id, out var p) && p.ManagerId == manager.Id && p.RouteId == req.EventTextAdvPlusRouteTableId), "Invalid text adventure page");
        Require(req.EventTextAdvPlusSelectOptionTableIdList.All(id => Tables.EventTextAdvPlusSelectOptionTable.TryGetValue(id, out var o) && o.ManagerId == manager.Id), "Invalid text adventure option");
        Require(req.AcquiredEventTextAdvPlusLootItemTableIdList.All(id => Tables.EventTextAdvPlusLootItemTable.TryGetValue(id, out var i) && i.ManagerId == manager.Id), "Invalid text adventure loot");
        Require(req.EventTextAdvPlusFirstSelectTableId == 0 || Tables.EventTextAdvPlusFirstSelectTable.TryGetValue(req.EventTextAdvPlusFirstSelectTableId, out var first) && first.ManagerId == manager.Id && first.RouteId == req.EventTextAdvPlusRouteTableId, "Invalid text adventure start");
        Require(req.EventTextAdvPlusSpecialIntroId == 0 || Tables.EventTextAdvPlusSpecialIntroTable.TryGetValue(req.EventTextAdvPlusSpecialIntroId, out var intro) && intro.ManagerId == manager.Id, "Invalid text adventure intro");
        if (state.ActiveRouteId == 0) return new() { PlayData = state.PlayData.Clone() };
        Require(state.ActiveRouteId == req.EventTextAdvPlusRouteTableId, "Text adventure route was not entered");
        var play = state.PlayData;
        Merge(play.EventTextAdvPlusEventPageTableIdList, req.EventTextAdvPlusEventPageTableIdList);
        Merge(play.EventTextAdvPlusSelectOptionTableIdList, req.EventTextAdvPlusSelectOptionTableIdList);
        Merge(play.AcquiredEventTextAdvPlusLootItemTableIdList, req.AcquiredEventTextAdvPlusLootItemTableIdList);
        Merge(play.BadgeRemovedEventTextAdvPlusLootItemTableIdList, req.CheckedEventTextAdvPlusLootItemTableIdList.Where(play.AcquiredEventTextAdvPlusLootItemTableIdList.Contains));
        if (req.EventTextAdvPlusSpecialIntroId != 0) Merge(play.EventTextAdvPlusSpecialIntroIdList, [req.EventTextAdvPlusSpecialIntroId]);
        play.PlayCount++;
        state.ActiveRouteId = 0;
        return new() { PlayData = play.Clone() };
    }

    private static bool ProgressEligible(TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, EventTextAdvPlusProgressRewardRecord_Raw reward)
    {
        if (reward.ManagerId != manager.Id || reward.GroupId != manager.RewardGroupId || state.ReceivedProgressRewards.Contains(reward.Id)) return false;
        var endings = Tables.EventTextAdvPlusEventPageTable.Values.Where(p => p.ManagerId == manager.Id && p.RouteId == reward.RouteId && p.PageType == EventTextAdvPlusPageType.EndingPage).ToArray();
        return endings.Length > 0 && endings.Count(p => state.PlayData.EventTextAdvPlusEventPageTableIdList.Contains(p.Id)) * 100 / endings.Length >= reward.RewardGoal;
    }

    private static bool EndingEligible(TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, EventTextAdvPlusEventPageRecord_Raw ending) =>
        ending.ManagerId == manager.Id && ending.PageType == EventTextAdvPlusPageType.EndingPage && ending.EndingAlbumReward > 0 &&
        state.PlayData.EventTextAdvPlusEventPageTableIdList.Contains(ending.Id) && !state.ReceivedEndingRewards.Contains(ending.Id);

    internal static bool HasReward(TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager) =>
        Tables.EventTextAdvPlusProgressRewardTable.Values.Any(r => ProgressEligible(state, manager, r)) ||
        Tables.EventTextAdvPlusEventPageTable.Values.Any(p => EndingEligible(state, manager, p));

    internal static ResClaimMiniGameTextAdventurePlusProgressReward ClaimProgress(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, IEnumerable<int> ids)
    {
        var rows = ids.Distinct().Select(id => Tables.EventTextAdvPlusProgressRewardTable.GetValueOrDefault(id))
            .Where(r => r != null && ProgressEligible(state, manager, r)).ToArray();
        Require(rows.All(r => Tables.RewardDataRecords.ContainsKey(r!.RewardId)), "Text adventure reward data is missing");
        var reward = RewardUtils.RegisterRewardsForUserDou(user, rows.Select(r => r!.RewardId).ToList());
        state.ReceivedProgressRewards.AddRange(rows.Select(r => r!.Id));
        ResClaimMiniGameTextAdventurePlusProgressReward response = new() { Reward = reward };
        response.ReceivedEventTextAdvPlusProgressRewardIdList.AddRange(state.ReceivedProgressRewards);
        return response;
    }

    internal static ResClaimMiniGameTextAdventurePlusEndingAlbumReward ClaimEnding(User user, TextAdventurePlusData state, EventTextAdvPlusManagerRecord_Raw manager, IEnumerable<int> ids)
    {
        var rows = ids.Distinct().Select(id => Tables.EventTextAdvPlusEventPageTable.GetValueOrDefault(id))
            .Where(p => p != null && EndingEligible(state, manager, p)).ToArray();
        Require(rows.All(p => Tables.RewardDataRecords.ContainsKey(p!.EndingAlbumReward)), "Text adventure reward data is missing");
        var reward = RewardUtils.RegisterRewardsForUserDou(user, rows.Select(p => p!.EndingAlbumReward).ToList());
        state.ReceivedEndingRewards.AddRange(rows.Select(p => p!.Id));
        ResClaimMiniGameTextAdventurePlusEndingAlbumReward response = new() { Reward = reward };
        response.ReceivedEventTextAdvPlusEventPageTableIdList.AddRange(state.ReceivedEndingRewards);
        return response;
    }

    internal static void Merge(RepeatedField<int> target, IEnumerable<int> values)
    {
        foreach (var id in values.Distinct()) if (!target.Contains(id)) target.Add(id);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new BadHttpRequestException(message, 400);
    }
}
