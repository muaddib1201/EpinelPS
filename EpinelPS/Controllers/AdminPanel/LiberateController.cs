using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Liberate;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.Controllers.AdminPanel;

[Route("admin/Users")]
public class LiberateController : Controller
{
    [HttpPost("ResetLiberate/{id}")]
    [ValidateAntiForgeryToken]
    public IActionResult ResetLiberate(ulong id)
    {
        if (!AdminController.CheckAuth(HttpContext)) return Unauthorized();
        var user = JsonDb.GetUser(id);
        if (user == null) return NotFound();

        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!user.LiberateDatas.TryGetValue(user.CurCharacterIdId, out var active))
                return Conflict(new { error = "no-active-liberation" });
            if (active.IsCompleted)
                return Conflict(new { error = "liberation-completed" });
            if (!GameData.Instance.LiberateCharacterTable.TryGetValue(active.CharacterId, out var character))
                return Conflict(new { error = "invalid-liberation-config" });

            var group = GameData.Instance.LiberateMissionGroupTable.Values.FirstOrDefault(g =>
                g.MissionStepGroup == character.MissionStepGroup && g.MissionStep == active.Step);
            if (group == null) return Conflict(new { error = "invalid-liberation-config" });
            var subgroups = GameData.Instance.LiberateMissionTable.Values
                .Where(m => m.GroupId == group.DefaultMissionGroupId).Select(m => m.SubGroupId).Distinct().ToArray();
            if (subgroups.Length == 0) return Conflict(new { error = "invalid-liberation-config" });

            var missions = LiberateHelper.GetMissions(active.CharacterId, group.DefaultMissionGroupId,
                subgroups[Random.Shared.Next(subgroups.Length)]);
            var previousMissions = active.MissionData.ToArray();
            int previousCount = active.RewardedCount;
            active.MissionData.Clear();
            active.MissionData.AddRange(missions);
            active.RewardedCount = 0;
            try { JsonDb.Save(); }
            catch
            {
                active.MissionData.Clear();
                active.MissionData.AddRange(previousMissions);
                active.RewardedCount = previousCount;
                throw;
            }
            return Json(new { characterId = active.CharacterId, missionCount = missions.Count });
        }
    }
}
