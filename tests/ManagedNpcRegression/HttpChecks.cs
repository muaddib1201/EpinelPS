using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using EpinelPS;
using EpinelPS.Controllers;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.Models;
using EpinelPS.Networking;
using EpinelPS.Services;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class HttpChecks
{
    public static async Task Run(CoreInfo core, GameData data, GameContext db, string connection)
    {
        if (!db.SdkUsers.Any(u => u.ID == 1)) db.SdkUsers.Add(new SdkUser { ID = 1, Email = "test-admin.invalid", PasswordHash = "unused", IsAdmin = true, PlayerName = "Human" });
        else db.SdkUsers.Find(1UL)!.IsAdmin = true;
        db.SaveChanges();
        core.Users.Single(u => u.ID == 1).userPointData.UserLevel = 40;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.AllowSynchronousIO = true; });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "test-keys"))).UseEphemeralDataProtectionProvider();
        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(connection));
        builder.Services.AddControllersWithViews(o => o.OutputFormatters.Insert(0, new ProtobufOutputFormatter()))
            .AddApplicationPart(typeof(GameContext).Assembly);
        await using var app = builder.Build();
        app.Use(async (ctx, next) => { if (ulong.TryParse(ctx.Request.Headers["Test-User"], out var uid)) ctx.Items["UserID"] = uid; await next(); });
        app.MapControllers(); await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        int guildId = core.Guilds.Keys.First();
        var join = await Send("/v1/guild/join", new ReqJoinGuild { Gsn = guildId }, ResJoinGuild.Parser);
        Check(join.Result == JoinGuildResult.Ok && core.Users.Single(u => u.ID == 1).Guild.guildId == guildId, "human must be able to join an open NPC union");
        var duplicate = await Send("/v1/guild/join", new ReqJoinGuild { Gsn = guildId }, ResJoinGuild.Parser);
        Check(duplicate.Result == JoinGuildResult.AlreadyJoined, "duplicate union join must be rejected");
        var leave = await Send("/v1/guild/leave", new ReqLeaveGuild { Gsn = guildId }, ResLeaveGuild.Parser);
        Check(leave.Result == LeaveGuildResult.Ok && leave.GuildLeaveAt > 0 && core.Users.Single(u => u.ID == 1).Guild.guildId == null, "human union leave must persist its timestamp");
        var rejoin = await Send("/v1/guild/join", new ReqJoinGuild { Gsn = guildId }, ResJoinGuild.Parser);
        Check(rejoin.Result == JoinGuildResult.UnableJoin, "union leave cooldown must block immediate rejoin");
        var human = core.Users.Single(u => u.ID == 1);
        human.Guild.LeaveAt = DateTime.UtcNow.AddSeconds(-6).Ticks;
        var afterWait = await Send("/v1/guild/join", new ReqJoinGuild { Gsn = guildId }, ResJoinGuild.Parser);
        Check(afterWait.Result == JoinGuildResult.Ok, "union rejoin cooldown must use the current five-second game setting");
        await Send("/v1/guild/leave", new ReqLeaveGuild { Gsn = guildId }, ResLeaveGuild.Parser);
        human.Guild.LeaveAt = null;
        var union = core.Guilds[guildId]; var previousLevel = union.Data.JoinLevel;
        union.Data.JoinLevel = 1000;
        var locked = await Send("/v1/guild/join", new ReqJoinGuild { Gsn = guildId }, ResJoinGuild.Parser);
        Check(locked.Result == JoinGuildResult.InvalidLevel, "union join level must be enforced");
        union.Data.JoinLevel = previousLevel;
        var founder = core.Users.First(u => u.Guild.guildId == guildId && u.Guild.MemberType == 1);
        var masterLeave = await Send("/v1/guild/leave", new ReqLeaveGuild { Gsn = guildId }, ResLeaveGuild.Parser, founder.ID);
        Check(masterLeave.Result == LeaveGuildResult.MasterCantLeave, "NPC master must not leave and orphan the union");
        var anonymous = await client.GetAsync("/admin/ManagedNpcs");
        Check(anonymous.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect, "NPC admin requires administrator authentication");
        var csrf = await client.PostAsync("/admin/ManagedNpcs/Configure", new FormUrlEncodedContent(new Dictionary<string,string> { ["Enabled"]="false",["Speed"]="1" }));
        Check((int)csrf.StatusCode is >=400 and <500, "NPC admin writes require antiforgery tokens");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccountController.CreateLauncherTokenForUser(1).Token);
        var page = await client.GetAsync("/admin/ManagedNpcs");
        if (page.StatusCode != HttpStatusCode.OK) Console.WriteLine("DIAG admin status: " + page.StatusCode + " " + await page.Content.ReadAsStringAsync());
        Check(page.StatusCode == HttpStatusCode.OK, "authenticated NPC admin page should render");
        var html = await page.Content.ReadAsStringAsync();
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Check(token.Length > 0 && html.Contains("托管 NPC"), "admin page must contain controls and CSRF token");
        var invalid = await client.PostAsync("/admin/ManagedNpcs/Configure", new FormUrlEncodedContent(new Dictionary<string,string> {
            ["Enabled"]="true",["Speed"]="99",["__RequestVerificationToken"]=token }));
        Check(invalid.StatusCode == HttpStatusCode.BadRequest && core.NpcManagement!.Speed == 1, "invalid speed must not change saved configuration");
        Console.WriteLine("PASS: HTTP union join/leave/cooldown/master protection and authenticated antiforgery-protected NPC admin");
        Console.WriteLine("Guild settings: " + string.Join(",", data.GameSettings.Where(p => p.Key.Contains("Guild") && (p.Key.Contains("Time") || p.Key.Contains("Join"))).Select(p => p.Key + "=" + p.Value.Value)));
        await app.StopAsync();

        async Task<T> Send<T>(string route, IMessage request, MessageParser<T> parser, ulong uid = 1) where T : IMessage<T>
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, route);
            message.Headers.Add("Test-User", uid.ToString());
            message.Content = new ByteArrayContent(request.ToByteArray()); message.Content.Headers.ContentType = new("application/octet-stream+protobuf");
            using var response = await client.SendAsync(message);
            Check(response.IsSuccessStatusCode, $"{route} returned {response.StatusCode}");
            return parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
}
