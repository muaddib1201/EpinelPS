using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EpinelPS.LobbyServer.Soloraid;

public sealed record RemoteSoloRaidSeason(int SeasonNo, string BossName, DateTimeOffset StartAt, DateTimeOffset EndAt)
{
    public bool IsValid => SeasonNo is > 0 and <= 10000 && !string.IsNullOrWhiteSpace(BossName) &&
        BossName.Length <= 128 && !BossName.Any(char.IsControl) && StartAt.Year >= 2022 && EndAt.Year <= 2100 &&
        EndAt > StartAt && EndAt - StartAt >= TimeSpan.FromHours(1) && EndAt - StartAt <= TimeSpan.FromDays(35);
}

/// <summary>Reads data from the public page; never executes its scripts or localized display text.</summary>
public static class NikkeHubSoloRaidSource
{
    public const string PageUrl = "https://nikkehub.com/zh-tw/raids/solo";
    public const int MaxHtmlBytes = 2 * 1024 * 1024;
    private const string PushMarker = "self.__next_f.push(";
    private static readonly Regex Scripts = new(@"<script\b[^>]*>(.*?)</script\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex ZonedDate = new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|[+-]\d\d:\d\d)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static RemoteSoloRaidSeason ParseHtml(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes) throw new InvalidDataException("NikkeHub page is too large");
        var models = new StringBuilder();
        foreach (Match script in Scripts.Matches(html))
        {
            string body = script.Groups[1].Value;
            int cursor = 0, start;
            while ((start = body.IndexOf(PushMarker, cursor, StringComparison.Ordinal)) >= 0)
            {
                cursor = start + PushMarker.Length;
                try
                {
                    using var reader = new JsonTextReader(new StringReader(body[cursor..])) { MaxDepth = 128, DateParseHandling = DateParseHandling.None };
                    var frame = JArray.Load(reader);
                    if (frame.Count >= 2 && frame[0].Type == JTokenType.Integer && (int)frame[0] == 1 && frame[1].Type == JTokenType.String)
                        models.Append((string?)frame[1]);
                }
                catch (JsonException) { /* Non-model scripts and malformed frames are not executable input. */ }
            }
        }
        var seasons = new List<JObject>();
        foreach (string line in models.ToString().Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 1) continue;
            string value = line[(colon + 1)..];
            if (!value.StartsWith('[') && !value.StartsWith('{')) continue;
            try
            {
                using var reader = new JsonTextReader(new StringReader(value)) { MaxDepth = 128, DateParseHandling = DateParseHandling.None };
                var model = JToken.Load(reader);
                seasons.AddRange(model.SelectTokens("$..season").OfType<JObject>()
                    .Where(s => (string?)s["kind"] == "solo"));
            }
            catch (JsonException) { }
        }
        if (seasons.Count == 0) throw new InvalidDataException("NikkeHub solo season model was not found");
        var parsed = seasons.Select(ParseSeason).Distinct().ToArray();
        if (parsed.Length != 1) throw new InvalidDataException("NikkeHub page has ambiguous solo seasons");
        return parsed[0];
    }

    private static RemoteSoloRaidSeason ParseSeason(JObject season)
    {
        if (season["seasonNo"]?.Type != JTokenType.Integer) throw new InvalidDataException("Missing solo season number");
        int number = (int)season["seasonNo"]!;
        if ((string?)season["id"] != "solo-" + number) throw new InvalidDataException("Inconsistent solo season identity");
        DateTimeOffset start = ParseDate(season["startAt"]);
        DateTimeOffset end = ParseDate(season["endAt"]);
        if (end.Ticks % TimeSpan.TicksPerSecond != 0 || end.Second is not (0 or 59))
            throw new InvalidDataException("Unsupported solo season cutoff precision");
        // The site currently publishes the inclusive :59 final second. Also accept an exact exclusive :00 boundary.
        if (end.Second == 59) end = end.AddSeconds(1);
        var bosses = season["bosses"] as JArray;
        if (bosses is not { Count: 1 } || bosses[0] is not JObject boss ||
            boss["nameKo"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)boss["bossId"]))
            throw new InvalidDataException("Missing or unsupported solo Boss metadata");
        var result = new RemoteSoloRaidSeason(number, ((string)boss["nameKo"]!).Trim(), start, end);
        if (!result.IsValid) throw new InvalidDataException("Invalid solo season dates or metadata");
        return result;
    }

    private static DateTimeOffset ParseDate(JToken? token)
    {
        string? value = token?.Type == JTokenType.String ? (string?)token : null;
        if (value is null || !ZonedDate.IsMatch(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) throw new InvalidDataException("Missing zoned ISO season date");
        return date.ToUniversalTime();
    }

    public static async Task<RemoteSoloRaidSeason> FetchAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, PageUrl);
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final.AbsoluteUri != PageUrl)
            throw new InvalidDataException("NikkeHub returned a different source URL");
        if (response.Content.Headers.ContentType?.MediaType != "text/html" ||
            response.Content.Headers.ContentLength is > MaxHtmlBytes) throw new InvalidDataException("Invalid NikkeHub response");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (output.Length + count > MaxHtmlBytes) throw new InvalidDataException("NikkeHub response is too large");
            output.Write(buffer, 0, count);
        }
        return ParseHtml(new UTF8Encoding(false, true).GetString(output.ToArray()));
    }
}
