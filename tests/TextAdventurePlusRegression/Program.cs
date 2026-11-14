using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

var root = Environment.GetEnvironmentVariable("NIKKE_WORKSPACE_ROOT")
    ?? throw new Exception("Use the isolated NIKKE test workflow");
var pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
var progress = new Progress<double>();
var managers = await data.LoadZip<EventTextAdvPlusManagerRecord_Raw>("EventTextAdvPlusManagerTable.json", progress);
var routes = await data.LoadZip<EventTextAdvPlusRouteRecord_Raw>("EventTextAdvPlusRouteTable.json", progress);
var pages = await data.LoadZip<EventTextAdvPlusEventPageRecord_Raw>("EventTextAdvPlusEventPageTable.json", progress);
var rewards = await data.LoadZip<EventTextAdvPlusProgressRewardRecord_Raw>("EventTextAdvPlusProgressRewardTable.json", progress);
if (managers.Length == 0 || routes.Length == 0 || pages.Length == 0) throw new Exception("Missing TextAdventurePlus static fixtures");
Console.WriteLine("Managers: " + JsonConvert.SerializeObject(managers));
Console.WriteLine("Routes: " + JsonConvert.SerializeObject(routes));
Console.WriteLine("Progress rewards: " + JsonConvert.SerializeObject(rewards));
Console.WriteLine("Endings: " + JsonConvert.SerializeObject(pages.Where(p => p.PageType == EventTextAdvPlusPageType.EndingPage).Select(p => new { p.Id, p.ManagerId, p.RouteId, p.EndingType, p.EndingAlbumReward })));

var user = new User { ID = 1 };
typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb", true)!.GetProperty("Instance")!
    .SetValue(null, new CoreInfo { Users = [user], BondRewardLevelsMigrated = true });
GameContext.InitializeOptions("Data Source=:memory:", "sqlite");
using var db = GameContext.CreateNew();
using var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
var manager = managers.Single(m => m.Id == 83501);
var entered = await Call<ResEnterMiniGameTextAdventurePlus>("/MiniGame/TextAdventurePlus/Enter",
    new ReqEnterMiniGameTextAdventurePlus { EventTextAdvPlusManagerTableId = manager.Id });
if (entered.PlayData == null) throw new Exception("FAIL: clicking the minigame returns no PlayData and leaves the client waiting");
if (entered.PlayData.PlayCount != 0 || entered.ReceivedEventTextAdvPlusProgressRewardIdList.Count != 0 || entered.ReceivedEventTextAdvPlusEventPageTableIdList.Count != 0)
    throw new Exception("New players must start without completed games or claimed rewards");
Console.WriteLine("PASS: minigame entry returns initialized player data");

async Task<T> Call<T>(string path, IMessage request) where T : IMessage<T>, new()
{
    var ctx = new DefaultHttpContext { RequestServices = services };
    ctx.Items["UserID"] = user.ID;
    using var input = new MemoryStream(request.ToByteArray());
    using var output = new MemoryStream();
    ctx.Request.Body = input;
    ctx.Response.Body = output;
    var handler = LobbyHandler.Handlers.TryGetValue(path, out var registered)
        ? (LobbyMessage)Activator.CreateInstance(registered.GetType())! : new EmptyHandler();
    await handler.HandleAsync(ctx);
    var response = new T();
    response.MergeFrom(output.ToArray());
    return response;
}
