using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EpinelPS;
using EpinelPS.Controllers.AdminPanel;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class AdminScheduleChecks
{
    internal static async Task<(int Passed, int Failed)> Run(GameData data, ulong userId, GameContext db, string workspace)
    {
        var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb", true)!;
        CoreInfo State() => (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
        var state = State();
        state.LauncherTokenKey = RandomNumberGenerator.GetBytes(32);
        db.SdkUsers.Find(userId)!.IsAdmin = true;
        db.SaveChanges();
        var claims = JArray.FromObject(state.Users);
        var clock = new ScheduleClock(new DateTime(2026, 11, 16, 6, 0, 0, DateTimeKind.Utc));
        var content = Path.Combine(workspace, "src/EpinelPS/EpinelPS");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AdminController).Assembly.FullName, ContentRootPath = content, WebRootPath = "wwwroot"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddDbContext<GameContext>(o => o.UseSqlite($"Data Source={db.Database.GetDbConnection().DataSource}"));
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "admin-schedule-keys")));
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(AdminController).Assembly);
        await using var app = builder.Build();
        app.UseStaticFiles();
        app.MapControllers();
        bool preview = Environment.GetEnvironmentVariable("NIKKE_EVENT_SCHEDULE_PREVIEW") == "1";
        string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local).WithKey(state.LauncherTokenKey, Encryption.SymmetricKey)
            .AddClaim("userId", userId).IssuedAt(DateTime.UtcNow).Expiration(DateTime.UtcNow.AddHours(2)).Encode();
        if (preview)
        {
            app.MapGet("/__preview/login", (Microsoft.AspNetCore.Http.HttpContext context) =>
            {
                context.Response.Cookies.Append("token", token, new() { HttpOnly = true, SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax });
                context.Response.Redirect("/admin/Events");
            });
        }
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var htmlResponse = await http.GetAsync("/admin/Events");
        string html = await htmlResponse.Content.ReadAsStringAsync();
        if (!htmlResponse.IsSuccessStatusCode) throw new InvalidOperationException($"Admin page failed: {htmlResponse.StatusCode}: {html[..Math.Min(500, html.Length)]}");
        string csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        if (csrf.Length == 0) throw new InvalidOperationException("Admin page did not render an antiforgery token");
        int passed = 0, failed = 0;
        await Test("admin presets save fixed 14, 20, 21 and custom-day countdowns", async () =>
        {
            foreach (var (days, expected) in new[] { (14, "2026-11-30T06:00:00Z"), (20, "2026-12-06T06:00:00Z"), (21, "2026-12-07T06:00:00Z"), (5, "2026-11-21T06:00:00Z") })
                CheckEnd(await Save("duration", days), expected);
        });
        await Test("explicit Beijing date is converted to UTC and persisted on the root only", async () =>
        {
            CheckEnd(await Save("absolute", end: "2030-01-03T03:59:59"), "2030-01-02T19:59:59Z");
            Check(State().EventEndUtc.Keys.SequenceEqual(new[] { 83500 }), "a main deadline was copied to child records");
        });
        await Test("extension adds days to the existing deadline and leaves activation unchanged", async () =>
        {
            var active = State().ActiveEventBannerIds.ToArray();
            CheckEnd(await Save("extend", 7), "2030-01-09T19:59:59Z");
            Check(State().ActiveEventBannerIds.SequenceEqual(active), "editing a deadline changed the active banners");
        });
        await Test("expired renewal starts at now and concurrent extensions are not lost", async () =>
        {
            clock.Set(new DateTime(2031, 2, 3, 4, 0, 0, DateTimeKind.Utc));
            CheckEnd(await Save("extend", 3), "2031-02-06T04:00:00Z");
            CheckEnd(await Save("duration", 20), "2031-02-23T04:00:00Z");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Save("extend", 1)));
            Check(State().EventEndUtc[83500] == new DateTime(2031, 3, 3, 4, 0, 0, DateTimeKind.Utc), "simultaneous extensions lost a day");
            clock.Set(new DateTime(2026, 11, 16, 6, 0, 0, DateTimeKind.Utc));
        });
        await Test("invalid days, dates, actions and banners cannot partially update configuration", async () =>
        {
            string before = JObject.FromObject(State()).ToString();
            foreach (var input in new[]
            {
                Fields("duration", 0), Fields("duration", -1), Fields("duration", 3651), Fields("duration"),
                Fields("nonsense", 14), Fields("absolute", end: "2020-01-01T03:59:59"),
                Fields("absolute", end: "2030-02-30T03:59:59"), Fields("absolute", end: "2030-01-03T03:59:59Z"),
                Fields("duration", 14, banner: -1)
            })
            {
                using var response = await Post("/admin/Events/Schedule", input);
                Check(response.StatusCode == HttpStatusCode.BadRequest, $"invalid request returned {response.StatusCode}");
                Check(JObject.FromObject(State()).ToString() == before, "rejected input changed configuration or claims");
            }
        });
        await Test("anonymous and missing-antiforgery requests cannot edit a deadline", async () =>
        {
            string before = JObject.FromObject(State()).ToString();
            using var missingCsrf = await http.PostAsync("/admin/Events/Schedule", new FormUrlEncodedContent(Fields("duration", 14)));
            Check(missingCsrf.StatusCode == HttpStatusCode.BadRequest, "missing antiforgery token was accepted");
            http.DefaultRequestHeaders.Authorization = null;
            using var anonymous = await Post("/admin/Events/Schedule", Fields("duration", 14));
            Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "anonymous deadline edit was accepted");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Check(JObject.FromObject(State()).ToString() == before, "unauthorized request changed configuration");
        });
        await Test("activation initializes an unset event for 14 days without resetting saved deadlines", async () =>
        {
            using var first = await Post("/admin/Events", new() { new("activeBannerIds", "10124"), new("activeBannerIds", "999000") });
            Check(first.IsSuccessStatusCode, "activation could not be saved");
            Check(State().EventEndUtc.GetValueOrDefault(999001) == new DateTime(2026, 11, 30, 6, 0, 0, DateTimeKind.Utc), "newly activated event did not get a fixed 14-day deadline");
            clock.Set(new DateTime(2026, 11, 17, 6, 0, 0, DateTimeKind.Utc));
            using var second = await Post("/admin/Events", new() { new("activeBannerIds", "999000") });
            Check(second.IsSuccessStatusCode && State().EventEndUtc[999001] == new DateTime(2026, 11, 30, 6, 0, 0, DateTimeKind.Utc), "resaving activation restarted an existing countdown");
            clock.Set(new DateTime(2026, 11, 16, 6, 0, 0, DateTimeKind.Utc));
        });
        await Test("saved deadlines survive reload and all existing player claims stay intact", async () =>
        {
            CheckEnd(await Save("duration", 20), "2026-12-06T06:00:00Z");
            jsonDb.GetMethod("Reload")!.Invoke(null, null);
            Check(State().EventEndUtc[83500] == new DateTime(2026, 12, 6, 6, 0, 0, DateTimeKind.Utc), "deadline was lost on configuration reload");
            Check(JToken.DeepEquals(claims, JArray.FromObject(State().Users)), "time edits changed player claims/progress");
            var saved = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db.json")));
            Check(JToken.DeepEquals(claims, saved["Users"]), "persisted claims/progress changed");
        });
        if (preview && failed == 0)
        {
            clock.Set(DateTime.UtcNow);
            Console.WriteLine($"PREVIEW {app.Urls.Single()}/__preview/login");
            await app.WaitForShutdownAsync();
        }
        await app.StopAsync();
        return (passed, failed);

        List<KeyValuePair<string, string>> Fields(string operation, int? days = null, string? end = null, int banner = 10124)
        {
            var fields = new List<KeyValuePair<string, string>> { new("BannerId", banner.ToString()), new("Operation", operation) };
            if (days != null) fields.Add(new("Days", days.Value.ToString()));
            if (end != null) fields.Add(new("EndBeijing", end));
            return fields;
        }
        Task<HttpResponseMessage> Post(string route, List<KeyValuePair<string, string>> fields)
        {
            fields.Add(new("__RequestVerificationToken", csrf));
            return http.PostAsync(route, new FormUrlEncodedContent(fields));
        }
        async Task<JObject> Save(string operation, int? days = null, string? end = null)
        {
            using var response = await Post("/admin/Events/Schedule", Fields(operation, days, end));
            string body = await response.Content.ReadAsStringAsync();
            Check(response.IsSuccessStatusCode, $"schedule route returned {response.StatusCode}: {body[..Math.Min(200, body.Length)]}");
            return JObject.Parse(body);
        }
        void CheckEnd(JObject response, string expected)
        {
            Check(response.Value<int>("eventId") == 83500, "response did not identify the main event");
            Check(response.Value<DateTime>("endUtc") == DateTime.Parse(expected).ToUniversalTime(), $"expected {expected}, received {response["endUtc"]}");
        }
        async Task Test(string name, Func<Task> run)
        {
            try { await run(); passed++; Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL: " + name + ": " + ex.Message); }
        }
    }
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private sealed class ScheduleClock(DateTime initial) : TimeProvider
    {
        private long ticks = initial.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Set(DateTime value) => Interlocked.Exchange(ref ticks, value.Ticks);
    }
}
