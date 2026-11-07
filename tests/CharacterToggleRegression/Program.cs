using System.Reflection;
using EpinelPS;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

// JsonDb uses the test executable's directory, never the deployed server's saves.
var dbType = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var dbProperty = dbType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!;
var save = dbType.GetMethod("Save", BindingFlags.Static | BindingFlags.Public)!;
var reload = dbType.GetMethod("Reload", BindingFlags.Static | BindingFlags.Public)!;

if (args.Contains("--verify-saved"))
{
    var loaded = await List();
    Toggle(loaded.CharacterBackgroundToggles, 1001, true);
    Toggle(loaded.CharacterInfoToggles, 1001, false);
    Toggle(loaded.FavoriteItemWallpaperToggles, 1001, true);
    Console.WriteLine("PASS: all three settings survived a fresh process startup");
    return;
}

int passed = 0;
List<string> failures = [];
await Check("dedicated background remains enabled after saving and reloading", async () =>
{
    Reset();
    await Background(1001, true);
    Reload();
    Toggle((await List()).CharacterBackgroundToggles, 1001, true);
});
await Check("turning background off persists an explicit false value", async () =>
{
    Reset();
    await Background(1001, true);
    await Background(1001, false);
    Reload();
    Toggle((await List()).CharacterBackgroundToggles, 1001, false);
});
await Check("legacy saves load with unchanged default toggle lists", async () =>
{
    Reset();
    var result = await List();
    Equal(0, result.CharacterBackgroundToggles.Count);
    Equal(0, result.CharacterInfoToggles.Count);
    Equal(0, result.FavoriteItemWallpaperToggles.Count);
});
await Check("characters keep independent background preferences without duplicates", async () =>
{
    Reset();
    await Background(1001, true);
    await Background(1002, false);
    await Background(1001, true);
    Reload();
    var result = await List();
    Equal(2, result.CharacterBackgroundToggles.Count);
    Toggle(result.CharacterBackgroundToggles, 1001, true);
    Toggle(result.CharacterBackgroundToggles, 1002, false);
});
await Check("show information and favorite wallpaper save independently of background", async () =>
{
    Reset();
    await Background(1001, true);
    await Send<ResToggleCharacterInfo>("characterinfo", new ReqToggleCharacterInfo { Csn = 1001, Toggle = true });
    Reload();
    Toggle((await List()).CharacterInfoToggles, 1001, true);
    await Send<ResToggleCharacterInfo>("characterinfo", new ReqToggleCharacterInfo { Csn = 1001, Toggle = false });
    await Send<ResToggleCharacterFavoriteItemWallpaper>("favoriteitemwallpaper",
        new ReqToggleCharacterFavoriteItemWallpaper { Csn = 1001, Toggle = true });
    Reload();
    var result = await List();
    Toggle(result.CharacterBackgroundToggles, 1001, true);
    Toggle(result.CharacterInfoToggles, 1001, false);
    Toggle(result.FavoriteItemWallpaperToggles, 1001, true);
    await Send<ResToggleCharacterFavoriteItemWallpaper>("favoriteitemwallpaper",
        new ReqToggleCharacterFavoriteItemWallpaper { Csn = 1001, Toggle = false });
    Reload();
    Toggle((await List()).FavoriteItemWallpaperToggles, 1001, false);
});
await Check("accounts with the same character serial keep separate preferences", async () =>
{
    Reset();
    await Background(1001, true);
    await Send<ResToggleCharacterBackground>("characterbackground",
        new ReqToggleCharacterBackground { Csn = 1001, Toggle = false }, 90002);
    Reload();
    Toggle((await List()).CharacterBackgroundToggles, 1001, true);
    var other = await Send<ResListCharacterToggle>("list", new ReqListCharacterToggle(), 90002);
    Toggle(other.CharacterBackgroundToggles, 1001, false);
});
await Check("unowned or zero character serial cannot create saved preferences", async () =>
{
    Reset();
    await Reject(() => Background(0, true));
    await Reject(() => Background(9999, true));
    await Reject(() => Send<ResToggleCharacterInfo>("characterinfo", new ReqToggleCharacterInfo { Csn = 9999, Toggle = true }));
    await Reject(() => Send<ResToggleCharacterFavoriteItemWallpaper>("favoriteitemwallpaper",
        new ReqToggleCharacterFavoriteItemWallpaper { Csn = 9999, Toggle = true }));
    Reload();
    var result = await List();
    Equal(0, result.CharacterBackgroundToggles.Count);
    Equal(0, result.CharacterInfoToggles.Count);
    Equal(0, result.FavoriteItemWallpaperToggles.Count);
});
await Check("detail preferences do not modify the separate lobby wallpaper setting", async () =>
{
    Reset();
    await Background(1001, true);
    Reload();
    var user = ((CoreInfo)dbProperty.GetValue(null)!).Users.Single(x => x.ID == 90001);
    Equal(5185, user.WallpaperList.Single().Id);
    if (user.WallpaperList.Single().UseCharacterBackground) throw new Exception("Lobby background was changed");
    Toggle((await List()).CharacterBackgroundToggles, 1001, true);
});

if (failures.Count > 0) throw new Exception($"FAIL: {string.Join("; ", failures)}");
Reset();
await Background(1001, true);
await Send<ResToggleCharacterInfo>("characterinfo", new ReqToggleCharacterInfo { Csn = 1001, Toggle = false });
await Send<ResToggleCharacterFavoriteItemWallpaper>("favoriteitemwallpaper",
    new ReqToggleCharacterFavoriteItemWallpaper { Csn = 1001, Toggle = true });
Console.WriteLine($"PASS: {passed} regression scenarios; saved fixture ready for fresh-process verification");

void Reset()
{
    var first = JsonConvert.DeserializeObject<User>("""
        {"ID":90001,"Characters":[{"Csn":1001},{"Csn":1002}],"WallpaperList":[{"Id":5185}]}
        """)!;
    var second = JsonConvert.DeserializeObject<User>("""
        {"ID":90002,"Characters":[{"Csn":1001}]}
        """)!;
    dbProperty.SetValue(null, new CoreInfo { Users = [first, second] });
    save.Invoke(null, null);
}
void Reload() => reload.Invoke(null, null);
Task<ResListCharacterToggle> List() => Send<ResListCharacterToggle>("list", new ReqListCharacterToggle());
Task<ResToggleCharacterBackground> Background(long csn, bool enabled) => Send<ResToggleCharacterBackground>(
    "characterbackground", new ReqToggleCharacterBackground { Csn = csn, Toggle = enabled });
void Toggle(IEnumerable<NetCharacterToggleInfo> values, long csn, bool expected)
{
    var value = values.SingleOrDefault(x => x.Csn == csn);
    if (value == null || value.Toggle != expected)
        throw new Exception($"Expected character {csn} toggle={expected}; got {value?.Toggle.ToString() ?? "missing saved state"}");
}
void Equal(int expected, int actual)
{
    if (expected != actual) throw new Exception($"Expected {expected}, got {actual}");
}
async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Expected an unowned character to be rejected");
}
async Task Check(string name, Func<Task> check)
{
    try { await check(); passed++; Console.WriteLine($"PASS: {name}"); }
    catch (Exception e) { failures.Add($"{name}: {e.Message}"); Console.WriteLine($"FAIL: {name}: {e.Message}"); }
}
async Task<T> Send<T>(string route, IMessage request, ulong userId = 90001) where T : IMessage<T>, new()
{
    using var services = new ServiceCollection()
        .AddSingleton(new GameContext(new DbContextOptionsBuilder<GameContext>().Options))
        .BuildServiceProvider();
    var context = new DefaultHttpContext { RequestServices = services };
    context.Items["UserID"] = userId;
    context.Request.Path = $"/v1/character/toggle/{route}";
    context.Request.Body = new MemoryStream(request.ToByteArray());
    context.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(context);
    var result = new T();
    result.MergeFrom(((MemoryStream)context.Response.Body).ToArray());
    return result;
}
