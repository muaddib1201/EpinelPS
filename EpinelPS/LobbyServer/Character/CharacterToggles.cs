using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Character;

[GameRequest("/character/toggle/characterbackground")]
public class ToggleCharacterBackground : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqToggleCharacterBackground>();
        var user = GetUser();
        CharacterToggleHelper.Set(user, user.CharacterBackgroundToggles, req.Csn, req.Toggle);
        await WriteDataAsync(new ResToggleCharacterBackground());
    }
}

[GameRequest("/character/toggle/characterinfo")]
public class ToggleCharacterInfo : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqToggleCharacterInfo>();
        var user = GetUser();
        CharacterToggleHelper.Set(user, user.CharacterInfoToggles, req.Csn, req.Toggle);
        await WriteDataAsync(new ResToggleCharacterInfo());
    }
}

[GameRequest("/character/toggle/favoriteitemwallpaper")]
public class ToggleCharacterFavoriteItemWallpaper : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqToggleCharacterFavoriteItemWallpaper>();
        var user = GetUser();
        CharacterToggleHelper.Set(user, user.FavoriteItemWallpaperToggles, req.Csn, req.Toggle);
        await WriteDataAsync(new ResToggleCharacterFavoriteItemWallpaper());
    }
}

[GameRequest("/character/toggle/list")]
public class ListCharacterToggle : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        await ReadData<ReqListCharacterToggle>();
        var user = GetUser();
        var response = new ResListCharacterToggle();
        response.CharacterInfoToggles.AddRange(user.CharacterInfoToggles.Select(
            x => new NetCharacterToggleInfo { Csn = x.Key, Toggle = x.Value }));
        response.CharacterBackgroundToggles.AddRange(user.CharacterBackgroundToggles.Select(
            x => new NetCharacterToggleInfo { Csn = x.Key, Toggle = x.Value }));
        response.FavoriteItemWallpaperToggles.AddRange(user.FavoriteItemWallpaperToggles.Select(
            x => new NetCharacterToggleInfo { Csn = x.Key, Toggle = x.Value }));
        await WriteDataAsync(response);
    }
}

internal static class CharacterToggleHelper
{
    public static void Set(User user, Dictionary<long, bool> toggles, long csn, bool enabled)
    {
        if (csn <= 0 || !user.Characters.Any(x => x.Csn == csn))
            throw new InvalidOperationException("Character is not owned by this user");

        toggles[csn] = enabled;
        JsonDb.Save();
    }
}
