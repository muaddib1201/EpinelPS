using EpinelPS.Database;
using EpinelPS.Models.Admin;
using EpinelPS.Services;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.Controllers.AdminPanel;

[Route("admin/ManagedNpcs")]
public class ManagedNpcsController(GameContext db) : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        if (!AdminController.CheckAuth(HttpContext)) return Unauthorized();
        lock (JsonDb.SaveLock)
            return View(new ManagedNpcAdminModel
            {
                Settings = ManagedNpcService.Clone(JsonDb.Instance.NpcManagement ?? new()),
                Players = JsonDb.Instance.Users.Where(u => u.ManagedNpc != null).Select(ManagedNpcService.Clone).ToList(),
                Guilds = JsonDb.Instance.Guilds.ToDictionary(g => g.Key, g => g.Value.Data.Name)
            });
    }

    [HttpPost("Create"), ValidateAntiForgeryToken]
    public IActionResult Create(NpcCreateForm form) => Write(() => ManagedNpcService.CreateBatch(db, form.Count, form.RequestId, DateTime.UtcNow));

    [HttpPost("Configure"), ValidateAntiForgeryToken]
    public IActionResult Configure(NpcConfigureForm form) => Write(() => ManagedNpcService.Configure(form.Enabled, form.Speed, DateTime.UtcNow));

    [HttpPost("Update"), ValidateAntiForgeryToken]
    public IActionResult Update(NpcUpdateForm form) => Write(() => ManagedNpcService.Update(form.Id, form.Enabled, form.Tier, form.GuildId, DateTime.UtcNow));

    private IActionResult Write(Action action)
    {
        if (!AdminController.CheckAuth(HttpContext)) return Unauthorized();
        if (!ModelState.IsValid) return BadRequest("参数无效，请检查数量、成长档次或速度倍率。");
        try { action(); return RedirectToAction(nameof(Index)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }
}
