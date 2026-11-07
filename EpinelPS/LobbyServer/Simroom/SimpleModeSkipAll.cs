using EpinelPS.LobbyServer.Mission;

namespace EpinelPS.LobbyServer.Simroom;

[GameRequest("/simroom/simplemode/skipall")]
public class SimpleModeSkipAll : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        await ReadData<ReqSkipAllSimRoomSimpleMode>();
        var user = GetUser();

        var response = MissionProgressDelivery.Apply(user, () =>
        {
            user.ResetDataIfNeeded();
            ResSkipAllSimRoomSimpleMode result = new() { Result = SimRoomResult.Success };
            user.ResetableData.SimRoomData.CurrentDifficulty = 5;
            user.ResetableData.SimRoomData.CurrentChapter = 3;
            var reward = SimRoomHelper.SimRoomReceivedReward(user, 5, 3); // 5 = difficulty, 3 = stage
            if (reward is not null)
            {
                result.Reward = reward;
                SimRoomHelper.RecordClear(user);
            }
            return result;
        });
        await WriteDataAsync(response);
    }
}
