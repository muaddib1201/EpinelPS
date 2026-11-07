using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using EpinelPS;
using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

// Maintenance helper: use the existing authenticated, CSRF-protected admin
// operation rather than editing the database underneath the live worker.
internal static class GuildAssignment
{
    public static async Task<int> Run(string root, string planPath)
    {
        var plan = JObject.Parse(File.ReadAllText(planPath));
        string save = Path.Combine(root, "Server and Server selector/db.json");
        var before = JObject.Parse(File.ReadAllText(save));
        ulong ownerId = (ulong)plan["OwnerId"]!;
        int guildId = (int)plan["GuildId"]!;
        var targets = plan["Targets"]!.ToArray();
        var owner = before["Users"]!.Single(u => (ulong)u["ID"]! == ownerId);
        if ((int?)owner["Guild"]?["guildId"] != guildId || (int?)owner["Guild"]?["MemberType"] != 1)
            throw new Exception("The owner is not the master of the requested union.");
        if (targets.Select(t => (ulong)t["Id"]!).Distinct().Count() != targets.Length)
            throw new Exception("Assignment plan contains duplicate NPCs.");
        foreach (var target in targets)
        {
            var user = before["Users"]!.Single(u => (ulong)u["ID"]! == (ulong)target["Id"]!);
            if (user["ManagedNpc"]?.Type != JTokenType.Object ||
                ((int?)user["Guild"]?["guildId"] is int previousGuild && previousGuild != guildId) ||
                (int?)user["Guild"]?["MemberType"] == 1 || (int?)user["ManagedNpc"]?["Tier"] != (int)target["Tier"]!)
                throw new Exception("A planned NPC is no longer available without changing another union.");
        }
        int missing = targets.Count(t => before["Users"]!.Single(u => (ulong)u["ID"]! == (ulong)t["Id"]!)["Guild"]?["guildId"]?.Type == JTokenType.Null);
        int existing = before["Users"]!.Count(u => (int?)u["Guild"]?["guildId"] == guildId);
        if (existing + missing > (int)plan["MemberCap"]!) throw new Exception("Assignment would exceed union capacity.");
        string backupRoot = Path.GetFullPath(Path.Combine(root, "backup"));
        string backup = Path.GetFullPath(Path.Combine(backupRoot, $"guild-npcs-{DateTime.UtcNow.AddHours(8):yyyyMMdd-HHmmss-fff}"));
        if (Path.GetDirectoryName(backup) != backupRoot) throw new Exception("Invalid backup location.");
        Directory.CreateDirectory(backup);
        File.Copy(save, Path.Combine(backup, "db.json"), false);
        File.Copy(planPath, Path.Combine(backup, "assignment-plan.json"), false);

        string token = new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local)
            .WithKey(Convert.FromBase64String((string)before["LauncherTokenKey"]!), Encryption.SymmetricKey)
            .AddClaim("userId", ownerId).Expiration(DateTime.UtcNow.AddMinutes(10)).Encode();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1"), Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        string html = await client.GetStringAsync("/admin/ManagedNpcs");
        string csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        if (csrf.Length == 0) throw new Exception("The live administrator page did not supply a CSRF token.");
        foreach (var target in targets)
        {
            var current = JObject.Parse(File.ReadAllText(save))["Users"]!.Single(u => (ulong)u["ID"]! == (ulong)target["Id"]!);
            if ((int?)current["Guild"]?["guildId"] == guildId) continue;
            using var response = await client.PostAsync("/admin/ManagedNpcs/Update", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = ((ulong)target["Id"]!).ToString(CultureInfo.InvariantCulture), ["Enabled"] = "true",
                ["Tier"] = ((int)target["Tier"]!).ToString(CultureInfo.InvariantCulture),
                ["GuildId"] = guildId.ToString(CultureInfo.InvariantCulture), ["__RequestVerificationToken"] = csrf
            }));
            if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.SeeOther))
                throw new Exception($"Assignment failed for NPC {(string)target["Name"]!}: {(int)response.StatusCode}");
        }
        var after = JObject.Parse(File.ReadAllText(save));
        var members = after["Users"]!.Where(u => (int?)u["Guild"]?["guildId"] == guildId).ToArray();
        if (members.Length != (int)plan["MemberCap"]! || !JToken.DeepEquals(owner, after["Users"]!.Single(u => (ulong)u["ID"]! == ownerId)))
            throw new Exception("Membership count or the human owner's save changed unexpectedly.");
        if (targets.Any(t => !members.Any(u => (ulong)u["ID"]! == (ulong)t["Id"]! && (bool?)u["ManagedNpc"]?["Enabled"] == true && (int?)u["Guild"]?["MemberType"] == 3)) ||
            (bool?)after["NpcManagement"]?["Enabled"] != true || after["Users"]!.Count(u => u["ManagedNpc"]?.Type == JTokenType.Object) != 100)
            throw new Exception("The assigned NPC population is incomplete or paused.");
        using var content = new ByteArrayContent(new ReqGetGuildPublicInfo { Gsn = guildId }.ToByteArray());
        content.Headers.ContentType = new("application/octet-stream+protobuf");
        using var publicResponse = await client.PostAsync("/v1/guild/publicinfo", content);
        publicResponse.EnsureSuccessStatusCode();
        var publicInfo = ResGetGuildPublicInfo.Parser.ParseFrom(await publicResponse.Content.ReadAsByteArrayAsync());
        if (publicInfo.Members.Count != members.Length || !publicInfo.Members.Any(m => (ulong)m.Usn == ownerId && m.Type == 1) ||
            targets.Any(t => !publicInfo.Members.Any(m => (ulong)m.Usn == (ulong)t["Id"]! && m.Type == 3)))
            throw new Exception("The game union roster does not match the saved assignment.");
        var receipt = new { Status = "verified", Guild = (string)plan["GuildName"]!, GuildId = guildId, Members = members.Length,
            Npcs = targets.Length, ExpectedDailyContribution = (int)plan["ExpectedDailyContribution"]!,
            OwnerAndLeadershipPreserved = true, GlobalAndIndividualManagementEnabled = true,
            ServerPopulation = 100, Backup = backup, Assigned = targets.Select(t => new { Id = (ulong)t["Id"]!, Name = (string)t["Name"]!, Tier = (int)t["Tier"]! }) };
        string result = JsonConvert.SerializeObject(receipt, Formatting.Indented);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath))!, "assignment-result.json"), result);
        Console.WriteLine(result);
        return 0;
    }
}
