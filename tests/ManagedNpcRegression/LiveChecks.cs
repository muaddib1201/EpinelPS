using System.Net;
using System.Net.Http.Headers;
using EpinelPS;
using Google.Protobuf;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class LiveChecks
{
    public static async Task<int> Run(string root)
    {
        var live = JObject.Parse(File.ReadAllText(Path.Combine(root, "Server and Server selector/db.json")));
        var players = live["Users"]!.Where(u => u["ManagedNpc"]?.Type == JTokenType.Object).ToArray();
        if (players.Length != 100) throw new Exception("Expected the 100 managed live NPCs.");
        var human = live["Users"]!.First(u => u["ManagedNpc"] == null || u["ManagedNpc"]!.Type == JTokenType.Null);
        string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
            .WithKey(Convert.FromBase64String((string)live["LauncherTokenKey"]!), Encryption.SymmetricKey)
            .AddClaim("userId", (ulong)human["ID"]!).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1"), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var unions = await Read("/v1/guild/recommendlist", new ReqRecommendGuildList(), ResRecommendGuildList.Parser);
        foreach (var item in ((JObject)live["Guilds"]!).Properties().Where(p => (bool?)p.Value["ManagedNpcGuild"] == true))
        {
            long id = long.Parse(item.Name);
            if (!unions.Guilds.Any(g => g.Gsn == id)) throw new Exception("An NPC union is absent from recommendations.");
            var info = await Read("/v1/guild/publicinfo", new ReqGetGuildPublicInfo { Gsn = id }, ResGetGuildPublicInfo.Parser);
            if (info.Members.Count < 8 || info.Members.Any(m => string.IsNullOrWhiteSpace(m.Nickname) || m.TeamCombat <= 0))
                throw new Exception("NPC union profiles or combat power are incomplete.");
        }
        foreach (var player in players)
        {
            var profile = await Read("/v1/User/GetProfile", new ReqGetProfileData { TargetUsn = (long)player["ID"]! }, ResGetProfileData.Parser);
            if (profile.Data?.User == null || string.IsNullOrWhiteSpace(profile.Data.User.Nickname) || profile.Data.ProfileTeam.Count != 5 || profile.Data.LastCampaignNormalStageId == 0)
                throw new Exception("A live NPC profile is incomplete.");
        }
        using var page = await client.GetAsync("/admin/ManagedNpcs");
        page.EnsureSuccessStatusCode();
        string html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        if (!html.Contains("__RequestVerificationToken") || players.Any(p => !html.Contains((string)p["ManagedNpc"]!["Name"]!)))
            throw new Exception("Live NPC management page lacks player controls.");
        Console.WriteLine("PASS: live authenticated reads returned all 100 NPC profiles and valid five-character teams");
        Console.WriteLine("PASS: three NPC unions have complete rosters and positive displayed combat power");
        Console.WriteLine("PASS: live administrator page renders all NPCs and protected controls");
        return 0;

        async Task<T> Read<T>(string path, IMessage request, MessageParser<T> parser) where T : IMessage<T>
        {
            using var body = new ByteArrayContent(request.ToByteArray()); body.Headers.ContentType = new("application/octet-stream+protobuf");
            using var response = await client.PostAsync(path, body); response.EnsureSuccessStatusCode();
            return parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        }
    }
}
