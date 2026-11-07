using EpinelPS.LobbyServer.Mission;

namespace EpinelPS.LobbyServer.Simroom;

[GameRequest("/simroom/simplemode/start")]
public class SimpleModeStart : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        await ReadData<ReqStartSimRoomSimpleMode>();
        User user = GetUser();

        var response = MissionProgressDelivery.Apply(user, () =>
        {
            user.ResetDataIfNeeded();
            var sim = user.ResetableData.SimRoomData;
            if (sim.CurrentDifficulty != 5 || sim.CurrentChapter != 3 || sim.CurrentSeasonData.IsOverclock ||
                sim.Events.Any(e => e.Location.Chapter != 3))
                sim.Events = [];
            sim.Entered = true;
            sim.CurrentDifficulty = 5;
            sim.CurrentChapter = 3;
            sim.CurrentSeasonData.IsOverclock = false;
            var events = SimRoomHelper.GetSimRoomEvents(user, 5, 3);
            foreach (var item in events)
            {
                if (item.EventCase == NetSimRoomEvent.EventOneofCase.Battle && item.Location.Order == 1)
                    SimRoomHelper.GetBuffOptions(user, item.Location);
            }
            ResStartSimRoomSimpleMode result = new() { Result = SimRoomResult.Success };
            result.Events.AddRange(sim.Events.Select(SimRoomHelper.MToNet));
            return result;
        });
        await WriteDataAsync(response);
    }
}
