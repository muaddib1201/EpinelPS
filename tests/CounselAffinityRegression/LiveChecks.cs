using System.Net.Http.Headers;
using EpinelPS;
using EpinelPS.Data;
using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Paseto;
using Paseto.Builder;

internal static class CounselAffinityLiveChecks
{
    internal static async Task<int> Run(string workspace,GameData data)
    {
        const ulong playerId = 1552239268;
        string livePath = Path.Combine(workspace,"Server and Server selector/db.json");
        var before = JObject.Parse(File.ReadAllText(livePath));
        var player = before["Users"]!.Single(u => u.Value<ulong>("ID") == playerId);
        var queen = player["BondInfo"]!.Single(b => b.Value<int>("NameCode") == 5185);
        var collected = queen["CounselDialogCompleteIds"]!.Values<int>().ToArray();
        int seenId = collected.First(id => data.AttractiveCounselTable.TryGetValue(id,out var row) && row.NameCode == 5185);
        var missing = player["BondInfo"]!.Select(b => new {
            NameCode = b.Value<int>("NameCode"), Ids = b["CounselDialogCompleteIds"]!.Values<int>().ToHashSet()
        }).SelectMany(b => data.AttractiveCounselTable.Values.Where(r => r.NameCode == b.NameCode && !b.Ids.Contains(r.Id))).FirstOrDefault();
        string token = new PasetoBuilder().Use(ProtocolVersion.V4,Purpose.Local)
            .WithKey(Convert.FromBase64String(before.Value<string>("LauncherTokenKey")!),Encryption.SymmetricKey)
            .AddClaim("userId",playerId).Expiration(DateTime.UtcNow.AddMinutes(5)).Encode();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1/v1/"), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",token);
        if (!(await Send<ResCounseledBefore>("character/counsel/check",new ReqCounseledBefore { CounselTid = seenId })).IsCounseledBefore)
            throw new InvalidOperationException("A collected live conversation is still reported unseen.");
        if ((await Send<ResCounseledBefore>("character/counsel/check",new ReqCounseledBefore { CounselTid = int.MaxValue })).IsCounseledBefore)
            throw new InvalidOperationException("An unknown live conversation is reported seen.");
        if (missing != null && (await Send<ResCounseledBefore>("character/counsel/check",new ReqCounseledBefore { CounselTid = missing.Id })).IsCounseledBefore)
            throw new InvalidOperationException("A missing live conversation is reported seen.");
        var journal = await Send<ResListAnsweredAttractiveCounsel>("character/counsel/answer/list",new ReqListAnsweredAttractiveCounsel { NameCode = 5185 });
        if (!journal.CorrectlyAnsweredAttractiveCounselTableIdList.SequenceEqual(collected))
            throw new InvalidOperationException("Live consultation journal changed.");
        var after = JObject.Parse(File.ReadAllText(livePath));
        foreach (var human in before["Users"]!.Where(u => u["ManagedNpc"] == null || u["ManagedNpc"]!.Type == JTokenType.Null))
        {
            var current = after["Users"]!.Single(u => u.Value<ulong>("ID") == human.Value<ulong>("ID"));
            if (!JToken.DeepEquals(human,current)) throw new InvalidOperationException("Read-only probes changed a human account.");
        }
        var report = new { status = "verified", mode = "read-only", seenId, missingId = missing?.Id, queenCollected = collected.Length,
            repeatHistory = "passed", journalPreserved = true, humanAccountsPreserved = true, clientVisualAcceptance = "pending" };
        File.WriteAllText(Path.Combine(workspace,".tools/counsel-affinity-20261108/live-readonly.json"),JsonConvert.SerializeObject(report,Formatting.Indented));
        Console.WriteLine("PASS live authenticated repeat-history, unchanged collection journal and human-account preservation");
        return 0;

        async Task<T> Send<T>(string route,IMessage body) where T:IMessage<T>,new()
        {
            using var content = new ByteArrayContent(body.ToByteArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
            using var response = await http.PostAsync(route,content);
            response.EnsureSuccessStatusCode();
            return (T)new T().Descriptor.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        }
    }
}
