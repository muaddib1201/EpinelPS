using EpinelPS.Data;
using EpinelPS.Database;
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
            && !userEvent.MissionIdList.Contains(de.Id)).ToList();

        NetRewardData reward = new();
        foreach (var record in records)
        {
            userEvent.MissionIdList.Add(record.Id);

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

        if (records.Count > 0)
        {
            userEvent.LastDate = DateTime.UtcNow.Ticks;
            JsonDb.Save();
        }

        await WriteDataAsync(new ResObtainDailyEventReward() { Reward = reward });
    }
}
