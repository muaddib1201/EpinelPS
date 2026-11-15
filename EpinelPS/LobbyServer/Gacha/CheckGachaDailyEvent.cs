using EpinelPS.Data;
using EpinelPS.Utils;
using EpinelPS.Database;
using EpinelPS.Services;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EpinelPS.LobbyServer.Gacha;

[GameRequest("/gacha/event/check")]
public class CheckGachaDailyEvent : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqCheckDailyFreeGacha req = await ReadData<ReqCheckDailyFreeGacha>();
               

        ResCheckDailyFreeGacha response = new ResCheckDailyFreeGacha();

        var user = GetUser();

        try
        {
            var evntFree = GameData.Instance.eventManagers[GameData.Instance.gachaTypes[req.GachaId].DailyFreeGachaEventId];
            long end = EventScheduleService.GetFixedEndUtc(JsonDb.Instance, evntFree.Id)?.Ticks ?? DateTime.Now.AddDays(20).Ticks;

            response.FreeCount = user.GachaDailyFreePulls.Contains(evntFree.Id) ? 0 : 1 ;
            response.EventData = new NetEventData()
            {
                Id = evntFree.Id,
                EventSystemType = (int)evntFree.EventSystemType,
                EventVisibleDate = DateTime.UtcNow.Subtract(TimeSpan.FromDays(7)).Ticks,
                EventStartDate = DateTime.UtcNow.Subtract(TimeSpan.FromDays(1)).Ticks,
                EventEndDate = end,
                EventDisableDate = end
            };
        }
        catch (Exception ex)
        {
            Logging.WriteLine("[CheckGachaDailyEvent] Event was not found in the database.");
        }

        await WriteDataAsync(response);
    }
}
