using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Event.Mission;

[GameRequest("/event/dailymission/obtainreward")]
public class ObtainDailyEventReward : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        // { "eventId": 20001, "dailyEventId": [ 200010105 ] }
        ReqObtainDailyEventReward req = await ReadData<ReqObtainDailyEventReward>();
        User user = GetUser();

        var result = MissionProgressDelivery.Apply(user, () => Claim(user, req));
        await WriteDataAsync(new ResObtainDailyEventReward { Reward = result });
    }

    private static NetRewardData Claim(User user, ReqObtainDailyEventReward req)
    {
        bool diary = req.EventId == AliceDiaryProgress.EventId;
        if (diary) AliceDiaryProgress.Reconcile(user);
        var history = diary ? MissionTriggerHistory.Read(user) : [];
        if (!user.EventMissionInfo.TryGetValue(req.EventId, out var userEvent))
        {
            userEvent = new EventMissionData();
            user.EventMissionInfo.Add(req.EventId, userEvent);
        }

        // Claimed daily event ids are stored in MissionIdList (never reset daily)
        // and reported back to the client through /event/mission/getclearlist
        var records = GameData.Instance.DailyEventTable.Values.Where(de =>
            de.EventId == req.EventId
            && req.DailyEventId.Contains(de.Id)
            && !userEvent.MissionIdList.Contains(de.Id))
            .OrderBy(de => diary ? de.Trigger switch { Trigger.EventPoint => 2, Trigger.DailyEventClear => 1, _ => 0 } : 0)
            .ToList();

        NetRewardData reward = new();
        foreach (var record in records)
        {
            if (diary && !AliceDiaryProgress.CanClaim(user, record, userEvent, history)) continue;
            userEvent.MissionIdList.Add(record.Id);
            userEvent.LastDate = DateTime.UtcNow.Ticks;

            RewardRecord? rewardRecord = GameData.Instance.GetRewardTableEntry(record.RewardId);
            if (rewardRecord?.Rewards != null)
            {
                foreach (var item in rewardRecord.Rewards)
                {
                    if (item.RewardType != RewardType.None)
                        RewardUtils.AddSingleObject(user, ref reward, item.RewardId, item.RewardType, item.RewardValue);
                }
            }

            // Day "all missions complete" entries count DailyEventClear on their phase group
            if (!record.IsMain)
                user.AddTrigger(Trigger.DailyEventClear, 1, record.EventPhaseGroupId);

            // The final reward counts EventPoint on the event id
            if (record.PointValue > 0)
                user.AddTrigger(Trigger.EventPoint, record.PointValue, record.EventId);
        }

        return reward;
    }
}
