using System.Net.Http.Headers;
using EpinelPS;
using Google.Protobuf;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class LiveChecks
{
    internal static async Task<int> Run(string root)
    {
        var savePath = Path.Combine(root, "Server and Server selector/db.json");
        var saved = JObject.Parse(await File.ReadAllTextAsync(savePath));
        var player = saved["Users"]!.First(u => u["ManagedNpc"]?.Type is null or JTokenType.Null);
        ulong id = player.Value<ulong>("ID");
        string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
            .WithKey(Convert.FromBase64String(saved.Value<string>("LauncherTokenKey")!), Encryption.SymmetricKey)
            .AddClaim("userId", id).IssuedAt(DateTime.UtcNow).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator })
            { BaseAddress = new Uri("https://127.0.0.1/v1/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var list = ResGetEventList.Parser.ParseFrom(await Send("event/list", new ReqGetEventList()));
        var joined = ResGetJoinedEvent.Parser.ParseFrom(await Send("event/getjoinedevent", new ReqGetJoinedEvent()));
        long end = new DateTime(2026, 11, 18, 19, 59, 59, DateTimeKind.Utc).Ticks;
        int[] ids = [83500, 40124, 40125, 60124, 100053, 83501, 83502, 83503];
        foreach (int eventId in ids)
        {
            foreach (var item in new[] { list.EventList.Single(e => e.Id == eventId), joined.EventWithJoinData.Single(e => e.EventData.Id == eventId).EventData })
                if (item.EventEndDate != end || item.EventDisableDate != end)
                    throw new InvalidOperationException($"Live event {eventId} has the wrong deadline.");
        }
        var after = JObject.Parse(await File.ReadAllTextAsync(savePath));
        var current = after["Users"]!.Single(u => u.Value<ulong>("ID") == id);
        if (!JToken.DeepEquals(player, current)) throw new InvalidOperationException("Read-only event verification changed the human save.");
        Console.WriteLine("PASS: both live event endpoints return 2026-11-18T19:59:59Z for all eight MARIA-n records");
        Console.WriteLine("PASS: authenticated read-only verification preserves the human save");
        return 0;

        async Task<byte[]> Send(string route, IMessage message)
        {
            using var body = new ByteArrayContent(message.ToByteArray());
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
            using var response = await http.PostAsync(route, body);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }
    }
}
