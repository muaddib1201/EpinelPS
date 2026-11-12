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
    private const int MaxModelFrames = 128;
    private const string PushMarker = "self.__next_f.push(";
    private static readonly Regex Scripts = new(@"<script\b[^>]*>(.*?)</script\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex ZonedDate = new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|[+-]\d\d:\d\d)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static RemoteSoloRaidSeason ParseHtml(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes) throw new InvalidDataException("NikkeHub page is too large");
        var models = new StringBuilder();
        int scriptCount = 0, frameCount = 0;
        foreach (Match script in Scripts.Matches(html))
        {
            if (++scriptCount > 256) throw new InvalidDataException("Too many NikkeHub scripts");
            string body = script.Groups[1].Value;
            int cursor = 0, start;
            while ((start = body.IndexOf(PushMarker, cursor, StringComparison.Ordinal)) >= 0)
            {
                if (++frameCount > MaxModelFrames) throw new InvalidDataException("Too many NikkeHub model frames");
                cursor = start + PushMarker.Length;
                try
                {
                    using var reader = new JsonTextReader(new StringSliceReader(body, cursor)) { MaxDepth = 128, DateParseHandling = DateParseHandling.None };
                    var frame = JArray.Load(reader);
                    if (frame.Count >= 2 && frame[0].Type == JTokenType.Integer && frame[0].ToString() == "1" && frame[1].Type == JTokenType.String)
                    {
                        string payload = (string)frame[1]!;
                        if (models.Length + payload.Length > MaxHtmlBytes) throw new InvalidDataException("NikkeHub models are too large");
                        models.Append(payload);
                    }
                }
                catch (JsonException) { /* Non-model scripts and malformed frames are not executable input. */ }
            }
        }
        var seasons = new List<JObject>();
        int recordCount = 0;
        foreach (string line in models.ToString().Split('\n'))
        {
            if (++recordCount > 8192) throw new InvalidDataException("Too many NikkeHub model records");
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
        if (!int.TryParse(season["seasonNo"]!.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            throw new InvalidDataException("Invalid solo season number");
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
        request.Headers.UserAgent.ParseAdd("EpinelPS-SoloRaidSync/1.0");
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

    // Read a JSON prefix directly from the original script instead of copying every remaining suffix.
    private sealed class StringSliceReader(string text, int offset) : TextReader
    {
        private int position = offset;
        public override int Peek() => position < text.Length ? text[position] : -1;
        public override int Read() => position < text.Length ? text[position++] : -1;
        public override int Read(char[] buffer, int index, int count)
        {
            int available = Math.Min(count, text.Length - position);
            text.CopyTo(position, buffer, index, available);
            position += available;
            return available;
        }
    }
}
