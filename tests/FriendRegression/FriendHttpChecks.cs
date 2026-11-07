using System.Net;
using EpinelPS;
using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.Models;
using EpinelPS.Networking;
using EpinelPS.Services;
using EpinelPS.Utils;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class FriendHttpChecks
{
    internal static async Task Run(User human, string database)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.AllowSynchronousIO = true; });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddDbContext<GameContext>(o => o.UseSqlite($"Data Source={database}"));
        builder.Services.AddControllersWithViews(o => o.OutputFormatters.Insert(0, new ProtobufOutputFormatter())).AddApplicationPart(typeof(GameContext).Assembly);
        await using var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            if (ulong.TryParse(ctx.Request.Headers["Test-User"], out var uid)) ctx.Items["UserID"] = uid;
            if (ctx.Request.Headers["Test-Nonce"].FirstOrDefault() is string nonce)
                ctx.Items[nameof(PacketDecryptResponse.Nonce)] = Convert.FromHexString(nonce);
            await next();
        });
        app.MapControllers(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using (var anonymous = await client.PostAsync("/v1/friend/get", Body(new ReqGetFriendData())))
            Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "anonymous friend request must be rejected");
        client.DefaultRequestHeaders.Add("Test-User", human.ID.ToString());
        var before = human.GetCurrencyVal(EpinelPS.Data.CurrencyType.FriendshipPoint);
        var bad = await Read("/v1/friend/sendfriendship", new ReqSendFriendshipPoint { Fusn = -1 }, ResSendFriendshipPoint.Parser);
        Check(bad.Result == FriendshipPointResult.FriendNotExist, "negative friend ID does not wrap to a player");
        client.DefaultRequestHeaders.Add("Test-Nonce", "0102030405060708");
        var first = await Read("/v1/friend/allfriendship", new ReqAllFriendshipPoint(), ResAllFriendshipPoint.Parser);
        var replay = await Read("/v1/friend/allfriendship", new ReqAllFriendshipPoint(), ResAllFriendshipPoint.Parser);
        Check(first.SendCount == 30 && first.ReceiveCount == 30, "HTTP all operation returns actual deltas");
        Check(first.ToByteArray().SequenceEqual(replay.ToByteArray()), "retransmitted packet returns its original reply without a second credit");
        Check(human.GetCurrencyVal(EpinelPS.Data.CurrencyType.FriendshipPoint) == before + 30, "HTTP replay cannot duplicate social currency");
        var list = await Read("/v1/friend/get", new ReqGetFriendData(), ResGetFriendData.Parser);
        Check(list.Friend.Count == 30 && list.SendFriendshipCount == 30 && list.ReceiveFriendshipCount == 30 && list.Friend.All(f => f.IsSend), "HTTP list reflects persisted sent and claimed status");
        await app.StopAsync();
        Console.WriteLine("PASS: authenticated HTTP protobuf list/gifts, invalid IDs and original-response packet replay without double credit");

        async Task<T> Read<T>(string route, IMessage request, MessageParser<T> parser) where T : IMessage<T>
        {
            using var response = await client.PostAsync(route, Body(request));
            Check(response.IsSuccessStatusCode, route + " returns success");
            return parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        }
    }

    private static ByteArrayContent Body(IMessage request)
    {
        var body = new ByteArrayContent(request.ToByteArray());
        body.Headers.ContentType = new("application/octet-stream+protobuf");
        return body;
    }
    private static void Check(bool success, string message) { if (!success) throw new Exception("FAIL: " + message); }
}
