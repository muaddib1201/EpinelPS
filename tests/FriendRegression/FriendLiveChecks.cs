using System.Net;
using System.Net.Http.Headers;
using EpinelPS;
using Google.Protobuf;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class FriendLiveChecks
{
    internal static async Task<int> Run(string root)
    {
        string liveFile = Path.Combine(root, "Server and Server selector", "db.json");
        var live = JObject.Parse(File.ReadAllText(liveFile));
        var human = live["Users"]!.Single(u => (ulong)u["ID"]! == 1552239268);
        var friends = human["Friendships"]!["Friends"]!.Select(t => (long)t).ToHashSet();
        long Balance(JToken user) => (long?)user["Currency"]?["FriendshipPoint"] ?? (long?)user["Currency"]?["4000"] ?? 0;
        long balance = Balance(human);
        Check(friends.Count == 30, "live save contains thirty NPC friends");
        string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
            .WithKey(Convert.FromBase64String((string)live["LauncherTokenKey"]!), Encryption.SymmetricKey)
            .AddClaim("userId", 1552239268UL).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1"), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var list = await Read("/v1/friend/get", new ReqGetFriendData(), ResGetFriendData.Parser);
        Check(list.Friend.Count == 30 && list.Friend.Select(f => f.User.Usn).ToHashSet().SetEquals(friends), "authenticated live list exposes exactly the saved thirty friends");
        Check(list.Friend.All(f => f.User.TeamCombat > 0 && !string.IsNullOrWhiteSpace(f.User.Nickname) && f.ReceiveState == 1 && !f.IsSend), "all real NPC profiles have a pending daily gift and can receive a human gift");
        Check(list.SendFriendshipCount == 0 && list.ReceiveFriendshipCount == 0, "live probe preserves all human daily send/claim allowances");
        var managed = ((JObject)live["Guilds"]!).Properties().Where(p => (bool?)p.Value["ManagedNpcGuild"] == true).ToArray();
        Check(managed.Length == 5, "live save contains five managed unions");
        foreach (var guild in managed)
        {
            var info = await Read("/v1/guild/publicinfo", new ReqGetGuildPublicInfo { Gsn = long.Parse(guild.Name) }, ResGetGuildPublicInfo.Parser);
            Check(info.Members.Count >= 8 && info.Members.Count(m => m.Type == 1) == 1, "each managed union has valid members and one master");
        }
        using var page = await client.GetAsync("/admin/ManagedNpcs"); page.EnsureSuccessStatusCode();
        string html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Check(html.Contains("积极养成") && html.Contains("OverClock") && html.Contains("貓咪補給站") && !html.Contains("普通活跃"), "live admin renders five unions and unified active growth");
        var after = JObject.Parse(File.ReadAllText(liveFile))["Users"]!.Single(u => (ulong)u["ID"]! == 1552239268);
        Check(Balance(after) == balance && (int)after["Friendships"]!["SentCount"]! == 0 && (int)after["Friendships"]!["ReceivedCount"]! == 0, "read probes neither grant currency nor spend human allowances");
        Console.WriteLine("PASS: live authenticated thirty-friend profiles and pending gifts, with full untouched human allowances and social balance");
        Console.WriteLine("PASS: live five-union rosters and unified active-growth administrator controls");
        return 0;

        async Task<T> Read<T>(string route, IMessage request, MessageParser<T> parser) where T : IMessage<T>
        {
            using var body = new ByteArrayContent(request.ToByteArray()); body.Headers.ContentType = new("application/octet-stream+protobuf");
            using var response = await client.PostAsync(route, body); response.EnsureSuccessStatusCode();
            return parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
}
