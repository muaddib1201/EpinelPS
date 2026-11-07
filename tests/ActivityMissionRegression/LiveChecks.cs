using System.Net.Http.Headers;
using System.Security.Cryptography;
using EpinelPS;
using Google.Protobuf;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class LiveChecks
{
    internal static async Task<int> Run(string root)
    {
        string path = Path.Combine(root, "Server and Server selector/db.json");
        var saved = JObject.Parse(File.ReadAllText(path));
        var human = saved["Users"]!.First(u => u["ManagedNpc"] == null || u["ManagedNpc"]!.Type == JTokenType.Null);
        ulong id = (ulong)human["ID"]!;
        long gsn = (long)human["Guild"]!["guildId"]!;
        var guild = saved["Guilds"]![gsn.ToString()]!;
        string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
            .WithKey(Convert.FromBase64String((string)saved["LauncherTokenKey"]!), Encryption.SymmetricKey)
            .AddClaim("userId", id).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1/v1/"), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var hall = await Read(client, "guild/get", new ReqGetGuild { Gsn = gsn }, ResGetGuild.Parser);
        var member = hall.Members.Single(m => m.Usn == (long)id);
        var now = DateTime.UtcNow;
        int reset = (int)saved["ResetHourUtcTime"]!;
        int day = int.Parse(now.AddHours(-reset).ToString("yyyyMMdd"));
        int first = int.Parse(now.AddHours(-reset).AddDays(-6).ToString("yyyyMMdd"));
        int Daily(JToken? history) => (int?)history?[day.ToString()] ?? 0;
        int Recent(JToken? history) => history is JObject obj ? obj.Properties().Where(p => int.Parse(p.Name) >= first && int.Parse(p.Name) <= day).Sum(p => (int)p.Value) : 0;
        Require(hall.Guild.Gsn == gsn && hall.Guild.ActivityPoint == (int)guild["Data"]!["ActivityPoint"]!, "live Union total differs from the migrated save");
        Require(hall.Guild.Exp == (int)guild["Data"]!["Exp"]! && hall.Guild.Grade == (int)guild["Data"]!["Grade"]!, "live Union experience differs from the save");
        Require(member.ActivityPointDaily == Daily(human["Guild"]!["ActivityByDay"]) && member.ActivityPointRecent == Recent(human["Guild"]!["ActivityByDay"]), "live human contribution differs from its dated ledger");
        Require(hall.Guild.ActivityPointDaily == Daily(guild["ActivityByDay"]) && hall.Guild.ActivityPointRecent == Recent(guild["ActivityByDay"]), "live Union daily/recent projection is incorrect");
        var rewards = await Read(client, "mission/getrewarded/all", new ReqGetRewardedData(), ResGetRewardedData.Parser);
        Require(rewards.DailyIds.SequenceEqual(human["ResetableData"]!["CompletedDailyMissions"]!.Values<int>()), "probe changed or lost daily reward claims");
        Console.WriteLine($"PASS authenticated Union/member projection: total={hall.Guild.ActivityPoint}, exp={hall.Guild.Exp}, human daily={member.ActivityPointDaily}, recent={member.ActivityPointRecent}; daily reward claims preserved");
        return 0;
    }

    private static async Task<T> Read<T>(HttpClient client, string route, IMessage request, MessageParser<T> parser) where T : IMessage<T>
    {
        using var body = new ByteArrayContent(request.ToByteArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
        using var response = await client.PostAsync(route, body);
        response.EnsureSuccessStatusCode();
        return parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
    }
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
