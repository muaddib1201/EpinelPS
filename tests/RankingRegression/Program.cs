using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
var data = new GameData(Directory.GetFiles(Path.Combine(root!.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var targets = await data.LoadZip<RankingRecord>("RankingTable.json", new Progress<double>());
var points = await data.LoadZip<RankingPointRecord>("RankingPointTable.json", new Progress<double>());
Console.WriteLine("TARGETS " + JsonConvert.SerializeObject(targets.GroupBy(x => x.RankingType).Select(g => new { type = g.Key.ToString(), count = g.Count(), first = g.Take(3), last = g.Last() })));
Console.WriteLine("POINTS " + JsonConvert.SerializeObject(points));
Console.WriteLine("RANK TRIGGERS " + JsonConvert.SerializeObject(data.TriggerTable.Values.Where(t => (int)t.Trigger is 31 or 32 or 33)));
Console.WriteLine("SERVER REWARDS " + JsonConvert.SerializeObject(targets.Take(3).Select(t => data.RewardDataRecords.GetValueOrDefault(t.ServerRewardId))));
string conn = $"Data Source={Path.Combine(AppContext.BaseDirectory, "ranking.db")}";
GameContext.InitializeOptions(conn, "sqlite");
using (var db = GameContext.CreateNew()) { db.Database.EnsureDeleted(); db.Database.EnsureCreated(); db.Users.Add(new GameUser { ID = 9801, Nickname = "Ranking9801" }); db.SaveChanges(); }
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"db.json"), JsonConvert.SerializeObject(new CoreInfo { Users = [new User { ID = 9801, LastHardStageCleared = 7001001 }] }));
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(conn)).BuildServiceProvider();
var ctx = new DefaultHttpContext { RequestServices = services };
ctx.Items["UserID"] = 9801UL;
ctx.Request.Path = "/v1/ranking/alltoprank";
ctx.Request.Body = new MemoryStream(new ReqGetAllTopRank().ToByteArray()); ctx.Response.Body = new MemoryStream();
await LobbyHandler.DispatchSingle(ctx);
var result = ResGetAllTopRank.Parser.ParseFrom(((MemoryStream)ctx.Response.Body).ToArray());
if (result.TopRankingList.Count != 5) { Console.WriteLine("FAIL overview must return the five implemented boards; actual="+result.TopRankingList.Count); return 1; }
Console.WriteLine("PASS overview returns five implemented boards");
return 0;
