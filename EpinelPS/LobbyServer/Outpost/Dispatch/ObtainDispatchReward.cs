using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.LobbyServer.Pass;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Outpost.Dispatch;

[GameRequest("/outpost/obtaindispatchreward")]
public class ObtainDispatchReward : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqObtainDispatchReward req = await ReadData<ReqObtainDispatchReward>();

        User user = GetUser();
        var response = MissionProgressDelivery.Apply(user, () => Obtain(user, req));
        await WriteDataAsync(response);
    }

    private static ResObtainDispatchReward Obtain(User user, ReqObtainDispatchReward req)
    {
        ResObtainDispatchReward response = new();
        NetRewardData rewardData = new();
        List<NetUserDispatchData> dispatchDatas = user.UserDispatchData.dispatchDatas ??= [];
        var completed = new List<DispatchRecord>();
        long now = DateTime.UtcNow.Ticks;

        foreach (var item in req.TidList.Distinct())
        {
            if (!GameData.Instance.DispatchTable.TryGetValue(item, out var dispatch))
                throw new BadHttpRequestException("Unknown dispatch.", 400);
            var running = dispatchDatas.FirstOrDefault(d => d.Tid == item);
            if (running == null && user.DispatchClearList.Contains(item)) continue;
            if (running == null || running.IsRun != 1 || running.EndAt <= 0 || running.EndAt > now)
                throw new BadHttpRequestException("Dispatch is not complete.", 400);
            completed.Add(dispatch);
        }
        foreach (var dispatch in completed)
        {
            int item = dispatch.Id;
            int rewardid = dispatch.RewardId;
            PassHelper.RewardsForUser(user, ref rewardData, rewardid);
            user.AddUnique(user.DispatchClearList, item);
            user.UserDispatchData.dispatchDatas.RemoveAll(x => x.Tid == item);
            dispatchDatas.RemoveAll(x => x.Tid == item);

            user.AddTrigger(Trigger.SendDispatch, 1, 0);
            user.AddTrigger(Trigger.SendDispatchGrade, 1, dispatch.DispatchGradeId);
        }


        List<NetSelectableDispatchData> dispatchtable = user.SelectableDispatchData.Where(x => user.DispatchClearList.Contains(x.SelectTid)).ToList();

        response.Reward = rewardData;
        response.DispatchList.AddRange(dispatchDatas);
        response.SelectableDispatchList.AddRange(dispatchtable);

        return response;
    }

}
