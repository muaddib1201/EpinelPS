using EpinelPS.Database;

namespace EpinelPS.Utils;

public static class WeeklyReset
{
    // The client starts the week when the game's Tuesday begins. With the default
    // reset hour of 20:00 UTC that day begins on Monday 20:00 UTC (Tuesday 04:00 UTC+8).
    // Labelling the game day by its UTC start date would move the boundary a day late.
    public static DateTime Start(DateTime utc) => Start(utc, JsonDb.Instance.ResetHourUtcTime);

    public static DateTime Start(DateTime utc, int resetHourUtc)
    {
        // Game day D begins at reset hour on the previous UTC date.
        DateTime gameDay = utc.AddHours(24 - resetHourUtc).Date;
        DateTime tuesday = gameDay.AddDays(-(((int)gameDay.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7));
        return tuesday.AddHours(resetHourUtc - 24);
    }
}
