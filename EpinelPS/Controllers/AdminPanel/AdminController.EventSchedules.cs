using System.Globalization;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models.Admin;
using EpinelPS.Services;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.Controllers.AdminPanel;

public partial class AdminController
{
    private void SaveEventActivation(List<int> ids)
    {
        lock (JsonDb.SaveLock)
        {
            var state = JsonDb.Instance;
            var previousActive = state.ActiveEventBannerIds;
            var previousEnd = state.EventEndUtc;
            try
            {
                foreach (int id in ids)
                    EventScheduleService.EnsureDefaultEndUtc(state, GameData.Instance.LobbyPrivateBannerTable[id].EventId, ScheduleNowUtc);
                state.ActiveEventBannerIds = ids;
                JsonDb.Save();
            }
            catch
            {
                state.ActiveEventBannerIds = previousActive;
                state.EventEndUtc = previousEnd;
                throw;
            }
        }
    }

    [HttpPost, Route("Events/Schedule"), ValidateAntiForgeryToken]
    public IActionResult SaveEventSchedule([FromForm] EventScheduleInput input)
    {
        if (!CheckAuth(HttpContext)) return Unauthorized();
        if (!ModelState.IsValid) return BadRequest(new { error = "events.schedule.invalid" });
        if (!GameData.Instance.LobbyPrivateBannerTable.TryGetValue(input.BannerId, out var banner) || banner.EventId <= 0)
            return BadRequest(new { error = "events.schedule.unknownEvent" });

        lock (JsonDb.SaveLock)
        {
            var state = JsonDb.Instance;
            DateTime now = ScheduleNowUtc, end;
            try
            {
                if (input.Operation is "duration" or "extend")
                {
                    if (input.Days is not (>= 1 and <= 3650))
                        return BadRequest(new { error = "events.schedule.invalidDays" });
                    DateTime basis = now;
                    if (input.Operation == "extend" && EventScheduleService.GetConfiguredEndUtc(state, banner.EventId) is DateTime existing && existing > now)
                        basis = existing;
                    end = basis.AddDays(input.Days.Value);
                }
                else if (input.Operation == "absolute")
                {
                    if (!DateTime.TryParseExact(input.EndBeijing, ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm"],
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                        return BadRequest(new { error = "events.schedule.invalidDate" });
                    end = new DateTimeOffset(local, TimeSpan.FromHours(8)).UtcDateTime;
                    if (end <= now || end > now.AddDays(3650)) return BadRequest(new { error = "events.schedule.invalidDate" });
                }
                else return BadRequest(new { error = "events.schedule.invalid" });
            }
            catch (ArgumentOutOfRangeException)
            {
                return BadRequest(new { error = "events.schedule.invalidDate" });
            }

            var original = state.EventEndUtc;
            EventScheduleService.SetEndUtc(state, banner.EventId, end);
            try { JsonDb.Save(); }
            catch { state.EventEndUtc = original; throw; }
            return Json(new { eventId = banner.EventId, endUtc = end, serverUtc = now });
        }
    }
}
