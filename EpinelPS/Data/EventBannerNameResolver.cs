using System.Text.Json;

namespace EpinelPS.Data;

/// <summary>Display-only titles for lobby story banners; selection still uses the original banner IDs.</summary>
public static class EventBannerNameResolver
{
    private static readonly IReadOnlyDictionary<string, EventTitle> Titles = LoadTitles();

    public static EventBannerNames Resolve(LobbyPrivateBannerRecord banner)
    {
        var resource = banner.PrivateBannerResource ?? "";
        if (Titles.TryGetValue(resource, out var title))
            return new(title.En, title.Zh, title.Ja, title.Ko, true);

        const string prefix = "btn_event_enter_";
        var identifier = resource.StartsWith(prefix, StringComparison.Ordinal) ? resource[prefix.Length..] : resource;
        if (string.IsNullOrWhiteSpace(identifier)) identifier = $"#{banner.EventId}";
        return new($"Name pending · {identifier}", $"名称待更新 · {identifier}",
            $"名称未登録 · {identifier}", $"이름 업데이트 대기 · {identifier}", false);
    }

    private static IReadOnlyDictionary<string, EventTitle> LoadTitles()
    {
        // Keep the small title snapshot in the executable: admin names must also work on
        // installations that only have Locale_Character.lsc, without a separate locale deployment.
        using var stream = typeof(EventBannerNameResolver).Assembly
            .GetManifestResourceStream("EpinelPS.Data.EventBannerTitles.json")
            ?? throw new InvalidOperationException("Missing event title catalog.");
        return JsonSerializer.Deserialize<Dictionary<string, EventTitle>>(stream)
            ?? throw new InvalidOperationException("Invalid event title catalog.");
    }

    private sealed record EventTitle(string LocaleKey, string En, string Zh, string Ja, string Ko);
}

public sealed record EventBannerNames(string English, string Chinese, string Japanese, string Korean, bool HasOfficialName)
{
    public string SearchText => string.Join(" ", English, Chinese, Japanese, Korean);
}
