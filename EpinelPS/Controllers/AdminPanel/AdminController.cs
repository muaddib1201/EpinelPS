using EpinelPS.Database;
using EpinelPS.Data;
using EpinelPS.Models.Admin;
using EpinelPS.Utils;
using EpinelPS.Services;
using Microsoft.AspNetCore.Mvc;
using Paseto;
using Paseto.Builder;
using System.Diagnostics;

namespace EpinelPS.Controllers.AdminPanel;

[Route("admin")]
public partial class AdminController(ILogger<AdminController> logger, TimeProvider? clock = null) : Controller
{
    private readonly ILogger<AdminController> _logger = logger;
    private DateTime ScheduleNowUtc => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public static bool CheckAuth(HttpContext context)
    {
        var db = context.RequestServices.GetRequiredService<GameContext>();
        string? token = context.Request.Cookies["token"] ?? context.Request.Headers.Authorization.ToString().Replace("Bearer ", "");

        try
        {
            PasetoTokenValidationResult encryptionToken = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
        .WithKey(JsonDb.Instance.LauncherTokenKey, Encryption.SymmetricKey)
        .Decode(token, new PasetoTokenValidationParameters() { ValidateLifetime = true });

            if (encryptionToken.IsValid)
            {
                var id = ((System.Text.Json.JsonElement)encryptionToken.Paseto.Payload["userId"]).GetUInt64();

                if (id == 0) return false;

                return db.SdkUsers.Where(x => x.ID == id && x.IsAdmin).Any();
            }
        }
        catch { }
        return false;
    }

    [Route("dashboard")]
    public IActionResult Dashboard()
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");

        return View();
    }
    [Route("Events")]
    public IActionResult Events()
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");

        return EventSelectionView();
    }

    [Route("Events"), ActionName("Events")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult EventsSave([FromForm] List<int>? activeBannerIds)
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");

        var ids = (activeBannerIds ?? []).Distinct().ToList();
        if (!ModelState.IsValid || ids.Any(id => !GameData.Instance.LobbyPrivateBannerTable.ContainsKey(id))) return BadRequest();
        SaveEventActivation(ids);

        TempData["MessageKey"] = "events.config.saved";
        return EventSelectionView();
    }

    [Route("Events/ActivateLatest")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ActivateLatestEvent()
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");

        var latest = GameData.Instance.LobbyPrivateBannerTable.Values.LastOrDefault();
        if (latest != null)
        {
            SaveEventActivation([latest.Id], keepSelectionIfExpired: true);
        }

        return Redirect("/admin/Events");
    }

    [Route("Configuration")]
    public IActionResult Configuration()
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");

        ServerConfiguration model = new()
        {
            LogType = JsonDb.Instance.LogLevel,
            EffectBands = JsonDb.Instance.EffectChangeBands ?? new()
        };

        return View(model);
    }

    [Route("Configuration"), ActionName("Configuration")]
    [HttpPost]
    public IActionResult ConfigurationSave([FromForm] ServerConfiguration cfg)
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");

        if (!ModelState.IsValid)
            return View(new ServerConfiguration { LogType = cfg.LogType, EffectBands = JsonDb.Instance.EffectChangeBands ?? new() });

        JsonDb.Instance.LogLevel = cfg.LogType;
        JsonDb.Save();
        return RedirectToAction(nameof(Configuration));
    }


    [HttpPost("Configuration/EffectChangeBands")]
    [ValidateAntiForgeryToken]
    public IActionResult SaveEffectChangeBands([Bind(Prefix = "EffectBands")] EffectChangeBands bands, bool restoreDefaults = false)
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");
        if (!restoreDefaults && !ModelState.IsValid)
            return View("Configuration", new ServerConfiguration { LogType = JsonDb.Instance.LogLevel, EffectBands = bands });

        lock (JsonDb.SaveLock)
        {
            var db = JsonDb.Instance;
            var previous = db.EffectChangeBands;
            db.EffectChangeBands = restoreDefaults ? null : bands;
            try { JsonDb.Save(); }
            catch { db.EffectChangeBands = previous; throw; }
        }
        TempData["EffectBandsSaved"] = restoreDefaults ? "已恢复默认档位。" : "已保存，下次效果变更时生效。";
        return RedirectToAction(nameof(Configuration));
    }

    [Route("Mail")]
    public IActionResult Mail()
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");

        return View();
    }
    [Route("Search")]
    public IActionResult Search()
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");

        return View();
    }
    [Route("Database")]
    public IActionResult Database()
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");

        return View();
    }

    [Route("SoloRaidMuseum")]
    public IActionResult SoloRaidMuseum()
    {
        if (!CheckAuth(HttpContext)) return Redirect("/admin/");
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
