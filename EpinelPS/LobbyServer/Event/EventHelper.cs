using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;
using log4net;
using Newtonsoft.Json;
using EpinelPS.Services;

namespace EpinelPS.LobbyServer.Event;

public class EventHelper
{
    private static readonly ILog log = LogManager.GetLogger(typeof(EventHelper));

    public static void AddEvents(User user, ref ResGetEventList response, DateTime nowUtc)
    {
        List<LobbyPrivateBannerRecord> lobbyPrivateBanners = GetLobbyPrivateBannerData(user, nowUtc);

        var eventManagers = GameData.Instance.eventManagers.Values.ToList();
        var schedule = EventScheduleService.CreateSnapshot(JsonDb.Instance);
        response.EventList.AddRange(MiracleSnowRecruitment.OpenPools(nowUtc).Select(pool => pool.Event));
        foreach (var banner in lobbyPrivateBanners)
        {
            // Get all events (including child events) associated with this banner
            List<NetEventData> events = GetEventData(banner, eventManagers, schedule);
            log.Debug($"Banner EventId: {banner.EventId} has {events.Count} associated events: {JsonConvert.SerializeObject(events)}");
            AddEvents(ref response, events, schedule, nowUtc);

        }
        // add daily mission events
        List<NetEventData> dailyMissionEvents = GetDailyMissionEventData(user, eventManagers, schedule, nowUtc);
        log.Debug($"Found {dailyMissionEvents.Count} associated daily mission events: {JsonConvert.SerializeObject(dailyMissionEvents)}");
        AddEvents(ref response, dailyMissionEvents, schedule, nowUtc);
    }

    public static void AddJoinedEvents(User user, ref ResGetJoinedEvent response, DateTime nowUtc)
    {
        List<LobbyPrivateBannerRecord> lobbyPrivateBanners = GetLobbyPrivateBannerData(user, nowUtc);

        var eventManagers = GameData.Instance.eventManagers.Values.ToList();
        var schedule = EventScheduleService.CreateSnapshot(JsonDb.Instance);
        response.EventWithJoinData.AddRange(MiracleSnowRecruitment.OpenPools(nowUtc)
            .Select(pool => new NetEventWithJoinData { EventData = pool.Event }));
        foreach (var banner in lobbyPrivateBanners)
        {

            // Get all events (including child events) associated with this banner
            var events = GetEventData(banner, eventManagers, schedule);
            log.Debug($"Banner EventId: {banner.EventId} has {events.Count} associated events: {JsonConvert.SerializeObject(events)}");
            AddJoinedEvents(ref response, events, schedule, nowUtc);

        }
        // add daily mission events
        List<NetEventData> dailyMissionEvents = GetDailyMissionEventData(user, eventManagers, schedule, nowUtc);
        log.Debug($"Found {dailyMissionEvents.Count} associated daily mission events: {JsonConvert.SerializeObject(dailyMissionEvents)}");
        AddJoinedEvents(ref response, dailyMissionEvents, schedule, nowUtc);
    }

    private static List<NetEventData> GetEventData(LobbyPrivateBannerRecord banner, List<EventManagerRecord> eventManagers, EventScheduleService.Snapshot schedule)
    {
        var main = eventManagers.FirstOrDefault(e => e.Id == banner.EventId);
        if (main == null)
        {
            Logging.WriteLine($"No event manager found for Banner EventId: {banner.EventId}", LogType.Warning);
            return [];
        }
        // Resolve the full group once per request, including nested and indirect recruitment children.
        return new[] { main }.Concat(eventManagers.Where(e => e.Id != main.Id && schedule.GetRootEventId(e.Id) == main.Id))
            .Where(e => !MiracleSnowRecruitment.IsScheduledEvent(e.Id))
            .Select(e => new NetEventData { Id = e.Id, EventSystemType = (int)e.EventSystemType }).ToList();
    }

    /// <summary>
    /// Get active lobby private banner data
    /// </summary>
    /// <returns>List of active lobby private banners</returns>
    public static List<LobbyPrivateBannerRecord> GetLobbyPrivateBannerData(User user, DateTime? asOfUtc = null)
    {
        var now = asOfUtc ?? DateTime.UtcNow;
        var ids = user.LobbyPrivateBannerIds is { Count: > 0 } personal
            ? personal : EventScheduleService.GetActiveBannerIds(now);
        var lobbyPrivateBannerRecords = GameData.Instance.LobbyPrivateBannerTable.Values;
        List<LobbyPrivateBannerRecord> lobbyPrivateBanners = [];

        lobbyPrivateBanners.AddRange(lobbyPrivateBannerRecords.Where(b => ids.Contains(b.Id)));
        if (lobbyPrivateBanners.Count == 0 && lobbyPrivateBannerRecords.LastOrDefault() is { } latest)
            lobbyPrivateBanners.Add(latest);
        lobbyPrivateBanners.RemoveAll(b => EventScheduleService.GetConfiguredEndUtc(JsonDb.Instance, b.EventId) <= now);
        Logging.WriteLine($"Found {lobbyPrivateBanners.Count} active lobby private banners.", LogType.Debug);
        log.Debug($"Active lobby private banners: {JsonConvert.SerializeObject(lobbyPrivateBanners)}");
        return lobbyPrivateBanners;
    }

    internal static DateTime GetEventStart(int eventId, DateTime nowUtc) =>
        JsonDb.Instance.ChallengeEventStartUtc.GetValueOrDefault(eventId, nowUtc.AddDays(-21));

    private static DateTime GetEventEnd(int eventId, DateTime nowUtc) =>
        EventScheduleService.GetEndUtc(JsonDb.Instance, eventId, nowUtc);

    private static void AddEvents(ref ResGetEventList response, List<NetEventData> eventDatas, EventScheduleService.Snapshot schedule, DateTime nowUtc)
    {
        foreach (var eventData in eventDatas)
        {
            // if (eventData.Id == 70115) continue;
            // Avoid adding duplicate events
            if (!response.EventList.Any(e => e.Id == eventData.Id))
            {
                long endDate = schedule.GetEndUtc(eventData.Id, nowUtc).Ticks;
                if (eventData.EventStartDate == 0) eventData.EventStartDate = GetEventStart(eventData.Id, nowUtc).Ticks;
                if (eventData.EventVisibleDate == 0) eventData.EventVisibleDate = nowUtc.AddDays(-21).Ticks;
                if (eventData.EventDisableDate == 0) eventData.EventDisableDate = endDate;
                if (eventData.EventEndDate == 0) eventData.EventEndDate = endDate;

                if (eventData.Id != 10046) // todo fix properly
                    response.EventList.Add(eventData);
            }
            else
            {
                log.Debug($"Skipping duplicate event Id: {eventData.Id}");
            }
        }
    }

    private static void AddJoinedEvents(ref ResGetJoinedEvent response, List<NetEventData> eventDatas, EventScheduleService.Snapshot schedule, DateTime nowUtc)
    {
        foreach (var eventData in eventDatas)
        {
            if (eventData.Id == 70115) continue;
            // Avoid adding duplicate events
            if (!response.EventWithJoinData.Any(e => e.EventData.Id == eventData.Id))
            {
                long endDate = schedule.GetEndUtc(eventData.Id, nowUtc).Ticks;
                if (eventData.EventStartDate == 0) eventData.EventStartDate = GetEventStart(eventData.Id, nowUtc).Ticks;
                if (eventData.EventVisibleDate == 0) eventData.EventVisibleDate = nowUtc.AddDays(-21).Ticks;
                if (eventData.EventDisableDate == 0) eventData.EventDisableDate = endDate;
                if (eventData.EventEndDate == 0) eventData.EventEndDate = endDate;
                response.EventWithJoinData.Add(new NetEventWithJoinData()
                {

                    EventData = eventData,
                    JoinAt = 0
                });
            }
            else
            {
                log.Debug($"Skipping duplicate event Id: {eventData.Id}");
            }
        }
    }

    private static List<NetEventData> GetDailyMissionEventData(User user, List<EventManagerRecord> eventManagers, EventScheduleService.Snapshot schedule, DateTime nowUtc)
    {
        List<NetEventData> events = [];

        // The start date must stay fixed: the client drops claimed missions created before it,
        // which made claimed rewards claimable again whenever the event list was refreshed
        long startDate = GetDailyMissionEventStartDate(user);

        var dailyEventIds = GameData.Instance.DailyMissionEventSettingTable.Values.Select(de => de.EventId).ToList();
        log.Debug($"Daily Mission Event IDs: {JsonConvert.SerializeObject(dailyEventIds)}");
        var dailyEvents = eventManagers.Where(em => dailyEventIds.Contains(em.Id)).ToList();
        log.Debug($"Found {dailyEvents.Count} daily events: {JsonConvert.SerializeObject(dailyEvents)}");
        if (dailyEvents.Count == 0)
        {
            Logging.WriteLine("No daily events found.", LogType.Warning);
            return events;
        }

        // Add each daily event to the list
        foreach (var dailyEvent in dailyEvents)
        {
            long endDate = schedule.GetEndUtc(dailyEvent.Id, nowUtc).Ticks;
            if (endDate <= nowUtc.Ticks) continue;
            events.Add(new NetEventData()
            {
                Id = dailyEvent.Id,
                EventSystemType = (int)dailyEvent.EventSystemType,
                EventStartDate = startDate,
                EventVisibleDate = startDate,
                EventDisableDate = endDate,
                EventEndDate = endDate
            });
        }
        return events;
    }

    private static long GetDailyMissionEventStartDate(User user)
    {
        using var ctx = GameContext.CreateNew();
        long registerTime = ctx.SdkUsers.Find(user.ID)?.RegisterTime ?? 0;
        DateTime registered = registerTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(registerTime).UtcDateTime : DateTime.UtcNow;

        // Same offset as the default start date of the other events
        return registered.AddDays(-21).Ticks;
    }

}
