using EpinelPS.Data;
using EpinelPS.Database;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.Controllers.AdminPanel;

[Route("admin/Users/{id}/Counsel")]
public class CounselController : Controller
{
    [HttpGet]
    public IActionResult Options(ulong id, int? nameCode, string? lang)
    {
        if (!AdminController.CheckAuth(HttpContext)) return Unauthorized();
        if (!ModelState.IsValid) return BadRequest();
        var user = JsonDb.GetUser(id);
        if (user == null) return NotFound();
        lock (user)
        {
            var consultable = GameData.Instance.AttractiveCounselTable.Values
                .Where(r => !string.IsNullOrEmpty(r.CounselGroupId)).Select(r => r.NameCode).ToHashSet();
            var owned = user.Characters.Select(c => GameData.Instance.CharacterTable.GetValueOrDefault(c.Tid))
                .Where(r => r != null && consultable.Contains(r.NameCode)).Select(r => r!)
                .DistinctBy(r => r.NameCode).ToArray();
            if (nameCode.HasValue && !owned.Any(r => r.NameCode == nameCode.Value)) return BadRequest();
            var characters = owned.Select(r => new
            {
                nameCode = r.NameCode,
                name = LocaleNameResolver.Resolve(r.NameLocalkey ?? "", lang)
            }).OrderBy(r => r.name).ToArray();
            var complete = user.BondInfo.FirstOrDefault(b => b.NameCode == nameCode)?.CounselDialogCompleteIds.ToHashSet() ?? [];
            var dialogues = Rows(nameCode ?? 0).Select((r, i) => new
            {
                id = r.Id,
                number = Number(r, i + 1),
                unlocked = complete.Contains(r.Id)
            }).ToArray();
            return Json(new { characters, dialogues });
        }
    }

    [HttpPost("unlock")]
    [ValidateAntiForgeryToken]
    public IActionResult Unlock(ulong id, int nameCode, int counselTid)
    {
        if (!AdminController.CheckAuth(HttpContext)) return Unauthorized();
        if (!ModelState.IsValid) return BadRequest();
        var user = JsonDb.GetUser(id);
        if (user == null) return NotFound();
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!JsonDb.Instance.Users.Contains(user)) return Conflict();
            if (!user.Characters.Any(c => GameData.Instance.CharacterTable.TryGetValue(c.Tid, out var r) && r.NameCode == nameCode))
                return BadRequest();
            var rows = Rows(nameCode);
            if (!rows.Any(r => r.Id == counselTid)) return BadRequest();
            var bond = user.BondInfo.FirstOrDefault(b => b.NameCode == nameCode);
            bool alreadyUnlocked = bond?.CounselDialogCompleteIds.Contains(counselTid) == true;
            if (!alreadyUnlocked)
            {
                bool created = bond == null;
                if (created)
                {
                    bond = new NetUserAttractiveData { NameCode = nameCode, Lv = 1, CanCounselToday = true };
                    user.BondInfo.Add(bond);
                }
                bond!.CounselDialogCompleteIds.Add(counselTid);
                try { JsonDb.Save(); }
                catch
                {
                    if (created) user.BondInfo.Remove(bond);
                    else bond.CounselDialogCompleteIds.Remove(counselTid);
                    throw;
                }
            }
            var complete = bond!.CounselDialogCompleteIds.ToHashSet();
            return Json(new { counselTid, alreadyUnlocked, collected = rows.Count(r => complete.Contains(r.Id)), total = rows.Length });
        }
    }

    private static AttractiveCounselRecord[] Rows(int nameCode) => GameData.Instance.AttractiveCounselTable.Values
        .Where(r => r.NameCode == nameCode && !string.IsNullOrEmpty(r.CounselGroupId)).OrderBy(r => r.Id).ToArray();

    private static int Number(AttractiveCounselRecord row, int fallback)
    {
        string? suffix = row.CounselGroupId?[(row.CounselGroupId.LastIndexOf('_') + 1)..];
        return int.TryParse(suffix, out int number) && number > 0 ? number : fallback;
    }
}
