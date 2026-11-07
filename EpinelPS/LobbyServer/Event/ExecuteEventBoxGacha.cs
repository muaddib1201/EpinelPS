using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Event;

[GameRequest("/event/boxgacha/execute")]
public class ExecuteEventBoxGacha : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        // from client: {"EventId":10051,"CurrentCount":1}
        ReqExecuteEventBoxGacha req = await ReadData<ReqExecuteEventBoxGacha>();
        User user = GetUser();

        ResExecuteEventBoxGacha response = new();

        EventBoxGachaRecord? box = GameData.Instance.EventBoxGachaTable.Values.FirstOrDefault(x => x.EventId == req.EventId);
        if (box == null)
        {
            Logging.WriteLine($"[BoxGacha] No box gacha for event {req.EventId}", LogType.Warning);
            await WriteDataAsync(response);
            return;
        }

        if (!user.EventBoxGachaInfo.TryGetValue(req.EventId, out EventBoxGachaData? data))
        {
            data = new EventBoxGachaData();
            user.EventBoxGachaInfo[req.EventId] = data;
        }

        if (req.CurrentCount != data.GachaCount)
            Logging.WriteLine($"[BoxGacha] Client count {req.CurrentCount} != server count {data.GachaCount} for event {req.EventId}", LogType.Warning);

        // the n-th draw uses the price and prob rows with Count == n
        int drawNumber = data.GachaCount + 1;

        List<EventBoxGachaRewardRecord> remaining = [.. GameData.Instance.EventBoxGachaRewardTable.Values
            .Where(x => x.Group == box.GachaRewardGroup && !data.RewardOrders.Contains(x.Order))];
        if (remaining.Count == 0)
        {
            Logging.WriteLine($"[BoxGacha] Box for event {req.EventId} is already empty", LogType.Warning);
            await WriteDataAsync(response);
            return;
        }

        // Costumes the user already owns are treated as drawn, the client shows them as obtained too.
        // If nothing else is left in the box, close it out without charging the user.
        List<EventBoxGachaRewardRecord> drawable = [.. remaining.Where(x => !IsOwnedCostume(user, x))];
        if (drawable.Count == 0)
        {
            data.RewardOrders.AddRange(remaining.Select(x => x.Order));
            data.GachaCount = drawNumber;
            response.RewardOrders.AddRange(data.RewardOrders);
            JsonDb.Save();
            await WriteDataAsync(response);
            return;
        }

        // pay for the draw (the first draw has no price)
        EventBoxGachaPriceRecord? price = GameData.Instance.EventBoxGachaPriceTable.Values
            .FirstOrDefault(x => x.Group == box.PriceGroup && x.Count == drawNumber);
        if (price != null && price.ItemType == RewardType.Item && price.ItemCount > 0)
        {
            // free tickets (from the ticket shop's free product) are spent before bought ones
            DbItemData? freeTicket = box.FreeTicketItemId == 0 ? null : user.Items.FirstOrDefault(x => x.ItemType == box.FreeTicketItemId);
            DbItemData? ticket = freeTicket != null && freeTicket.Count >= price.ItemCount
                ? freeTicket
                : user.Items.FirstOrDefault(x => x.ItemType == price.ItemId);
            if (ticket == null || ticket.Count < price.ItemCount)
            {
                Logging.WriteLine($"[BoxGacha] Not enough tickets {price.ItemId}: have {ticket?.Count ?? 0}, need {price.ItemCount}", LogType.Warning);
                await WriteDataAsync(response);
                return;
            }

            user.RemoveItemBySerialNumber(ticket.Isn, price.ItemCount);
            response.Ticket = NetUtils.UserItemDataToNet(ticket);
        }

        EventBoxGachaRewardRecord picked = PickReward(drawable, box.ProbGroup, drawNumber);

        NetRewardData reward = new();
        RewardUtils.AddSingleObject(user, ref reward, picked.ItemId, picked.ItemType, picked.ItemCount);
        response.Reward = reward;

        data.RewardOrders.Add(picked.Order);
        data.GachaCount = drawNumber;

        // last drawable item taken, only owned costumes are left: close the box
        if (drawable.Count == 1)
            data.RewardOrders.AddRange(remaining.Where(x => x != picked).Select(x => x.Order));
        response.RewardOrders.AddRange(data.RewardOrders);

        JsonDb.Save();
        await WriteDataAsync(response);
    }

    private static bool IsOwnedCostume(User user, EventBoxGachaRewardRecord reward)
    {
        if (reward.ItemType != RewardType.CharacterCostume) return false;

        // /character/costume/get currently reports every costume as owned, so mirror that here
        return user.CostumeList.Contains(reward.ItemId) || GameData.Instance.CharacterCostumeTable.ContainsKey(reward.ItemId);
    }

    private static EventBoxGachaRewardRecord PickReward(List<EventBoxGachaRewardRecord> candidates, int probGroup, int drawNumber)
    {
        // Rates are weights; items already drawn are removed and the rest are renormalized
        List<EventBoxGachaProbRecord> probs = [.. GameData.Instance.EventBoxGachaProbTable.Values.Where(x => x.Group == probGroup)];
        int maxCount = probs.Count == 0 ? 0 : probs.Max(x => x.Count);
        int count = Math.Min(drawNumber, maxCount);

        List<(EventBoxGachaRewardRecord reward, int weight)> weighted = [.. candidates
            .Select(c => (c, probs.FirstOrDefault(p => p.Count == count && p.Order == c.Order)?.Rate ?? 0))];

        int total = weighted.Sum(x => x.weight);
        if (total <= 0)
        {
            // nothing has a rate at this count, fall back to a uniform pick
            return candidates[Rng.Next(0, candidates.Count)];
        }

        int roll = Rng.Next(0, total);
        foreach ((EventBoxGachaRewardRecord reward, int weight) in weighted)
        {
            if (roll < weight) return reward;
            roll -= weight;
        }

        return weighted.Last(x => x.weight > 0).reward;
    }
}
