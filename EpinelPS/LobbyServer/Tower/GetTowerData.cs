using EpinelPS.Data;
using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Tower;

[GameRequest("/tower/gettowerdata")]
public class GetTowerData : LobbyMessage
{
    private const long TicksPerHour = 36_000_000_000;
    private const long OpenDuration = 863_990_000_000; // 23:59:59

    /// <summary>
    /// Builds the open schedule of a tower from TowerInfoTable.
    /// The table lists game days (0 = Sunday). A game day begins at the daily reset
    /// (ResetHourUtcTime, 20:00 UTC on the official server) of the previous UTC weekday,
    /// and the client evaluates DayOfWeek schedules in UTC, so each day is shifted back by one.
    /// </summary>
    public static NetSchedule BuildSchedule(TowerInfoRecord info)
    {
        List<int> days = info.DayOfWeek ?? [];
        if (days.Distinct().Count() >= 7)
            return new NetSchedule { AllTime = new() };

        NetDayOfWeekSchedule schedule = new()
        {
            StartTime = JsonDb.Instance.ResetHourUtcTime * TicksPerHour,
            Duration = OpenDuration
        };
        schedule.DayOfWeeks.AddRange(days.Select(d => ((d % 7) + 6) % 7).Distinct().OrderBy(d => d));
        return new NetSchedule { DayOfWeek = schedule };
    }

    protected override async Task HandleAsync()
    {
        ReqGetTowerData req = await ReadData<ReqGetTowerData>();
        ResGetTowerData response = new();
        User user = GetUser();

        Dictionary<CorporationTowerType, int> counts = user.ResetableData.TowerCount ?? [];
        user.TowerProgress.Remove(0);

        foreach (TowerInfoRecord info in GameData.Instance.TowerInfoTable.Values.OrderBy(i => (int)i.Id))
        {
            NetTowerData data = new() { Type = (int)info.Id };

            if (info.ClearLimit > 0)
            {
                counts.TryGetValue(info.Id, out int used);
                data.RemainCount = Math.Max(0, info.ClearLimit - used);
            }

            data.Schedules.Add(BuildSchedule(info));

            if (user.TowerProgress.TryGetValue(info.Id, out int floor))
                data.Floor = floor;

            response.Data.Add(data);
        }

        await WriteDataAsync(response);
    }
}
