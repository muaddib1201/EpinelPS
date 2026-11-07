using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer;
using EpinelPS.Models;
using EpinelPS.Networking;
using EpinelPS.Services;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

// The test web server uses the actual MVC routes, protobuf binder and an isolated save directory.
const int eventId = 60124, first = 212410101, second = first + 1, ticket = 7110124;
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache/prdenv/153-cb882d4b30"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await Load("EventManagerTable", data.eventManagers, x => x.Id);
await Load("LobbyPrivateBannerTable", data.LobbyPrivateBannerTable, x => x.Id);
await Load("EventStoryTable", data.EventStoryTable, x => x.Id);
await Load("EventDungeonTable", data.EventDungeonTable, x => x.Id);
await Load("EventDungeonDifficultTable", data.EventDungeonDifficultTable, x => x.Id);
await Load("EventDungeonStageTable", data.EventDungeonStageTable, x => x.Id);
await Load("EventDungeonSpotBattleTable", data.EventDungeonSpotBattleTable, x => x.Id);
await Load("AutoChargeTable", data.AutoChargeTable, x => x.Id);
await Load("RewardTable", data.RewardDataRecords, x => x.Id);
var conn = $"Data Source={Path.Combine(AppContext.BaseDirectory, "challenge-regression.db")}";
GameContext.InitializeOptions(conn, "sqlite");
var infoProperty = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
bool verifySaved = args.Contains("--verify-saved");
var info = verifySaved ? JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))! :
    new CoreInfo { ActiveEventBannerIds = [10124], ResetHourUtcTime = 20 };
infoProperty.SetValue(null, info);
if (!verifySaved)
{
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
}
var builder = WebApplication.CreateBuilder();
var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero) };
builder.Services.AddSingleton<TimeProvider>(clock);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.AllowSynchronousIO = true; });
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(conn));
builder.Services.AddControllers(o => o.OutputFormatters.Insert(0, new ProtobufOutputFormatter()))
    .AddApplicationPart(typeof(LobbyHandler).Assembly);
await using var app = builder.Build();
app.Use(async (context, next) => {
    if (ulong.TryParse(context.Request.Headers["Test-User"], out var id)) context.Items["UserID"] = id;
    if (context.Request.Headers.TryGetValue("Test-Nonce", out var value))
        context.Items["Nonce"] = Convert.FromHexString(value.ToString());
    await next();
});
app.MapControllers();
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/v1/event/challengestage/") };
int cases = 0, failures = 0;
if (verifySaved)
{
    await Test("fresh process reads the saved clear, tickets and reward", async () => {
        var user = info.Users.Single(x => x.ID == 92001);
        var s = await Get(user);
        Check(s.RemainTicket == 0 && s.LastClearedEventStageList.Single().StageId == first, "saved state");
        Check(user.GetCurrencyVal(CurrencyType.CharPremiumTicket) == 1, "saved recruitment voucher");
        await Send(user, "clear", ClearReq());
        Check(user.GetCurrencyVal(CurrencyType.CharPremiumTicket) == 1, "no reward replay after restart");
    });
}
else
{
    await Test("legacy placeholder opens at the configured cap and displays the challenge squad", async () => {
        var u = New(92010);
        u.EventInfo[eventId] = new EventData();
        u.UserTeams[(int)TeamType.ChallengeModeEvent] = new() { Type = 21, LastContentsTeamNumber = 2 };
        u.UserTeams[(int)TeamType.StoryEvent] = new() { Type = 20, LastContentsTeamNumber = 1 };
        var s = await Get(u);
        Check(s.RemainTicket == 1 && s.LastClearedEventStageList.Count == 0, "placeholder must yield one ticket and no bogus stage");
        Check(s.TeamData.Type == 21 && s.TeamData.LastContentsTeamNumber == 2, "dedicated squad");
    });
    await Test("enter, defeat and retreat leave attempts and inventory intact", async () => {
        var u = New(92011);
        foreach (int result in new[] { 0, 2 }) {
            await Ok(u, "enter", EnterReq());
            var s = ResClearChallengeEventStage.Parser.ParseFrom(await Ok(u, "clear", ClearReq(result:result)));
            Check(s.RemainTicket == 1 && (s.Reward?.Currency.Count ?? 0) == 0 && s.FirstClearReward == null, "failed battle paid or charged");
        }
        Check((await Get(u)).LastClearedEventStageList.Count == 0 && u.Currency.GetValueOrDefault(CurrencyType.Gold) == 0, "defeat advanced progress");
    });
    await Test("victory persists both table rewards and a duplicate clear cannot pay twice", async () => {
        var u = New(92001);
        await Ok(u, "enter", EnterReq());
        Check((await Get(u)).RemainTicket == 1, "entry spent a ticket");
        var s = ResClearChallengeEventStage.Parser.ParseFrom(await Ok(u, "clear", ClearReq()));
        Check(s.RemainTicket == 0 && s.Reward.Currency.Single(x=>x.Type==2000).Value == 9660, "regular reward incorrect");
        Check(s.FirstClearReward.Currency.Single(x=>x.Type==5100).Value == 1 && u.Currency[(CurrencyType)2000] == 28980, "first clear reward incorrect");
        var progress = (await Get(u)).LastClearedEventStageList.Single();
        Check(progress.StageId == first && progress.DifficultyId == 21241, "actual cleared difficulty and stage");
        await Send(u,"clear",ClearReq());
        await Reject(u,"enter",EnterReq());
        using var db=GameContext.CreateNew();
        Check(db.Triggers.Count(x=>x.UserId==u.ID && x.Type==Trigger.EventStageClear && x.ConditionId==first)==1, "stage trigger count");
        Check(db.Triggers.Single(x=>x.UserId==u.ID && x.Type==Trigger.EventDungeonStageClear && x.ConditionId==eventId).Value==1, "event trigger count");
        Check(u.Currency[(CurrencyType)5100]==1 && u.Currency[(CurrencyType)2000]==28980, "replay changed balance");
    });
    await Test("unentered, mismatched, locked and foreign battles cannot grant progress", async () => {
        var u=New(92012);
        await Reject(u,"clear",ClearReq());
        await Reject(u,"enter",EnterReq(second));
        await Reject(u,"enter",new ReqEnterChallengeEventStage{EventId=eventId, StageId=1, TeamNumber=1});
        await Reject(u,"get",new ReqChallengeEventStageData{EventId=83500});
        await Ok(u,"enter",EnterReq());
        await Reject(u,"clear",ClearReq(team:2));
        await Reject(u,"clear",ClearReq(second));
        Check(u.Currency.GetValueOrDefault(CurrencyType.Gold)==0 && (await Get(u)).RemainTicket==1, "invalid request changed state");
        await Ok(u,"clear",ClearReq());
    });
    await Test("only the active banner's challenge is playable", async () => {
        var u=New(92013);
        u.LobbyPrivateBannerIds=[10123];
        await Reject(u,"get",new ReqChallengeEventStageData{EventId=eventId});
        Check(!u.EventInfo.ContainsKey(eventId),"inactive event initialized");
    });
    await Test("daily refresh caps free attempts, permits repeated wins and saves earned items", async () => {
        var u=New(92014);
        await Win(u,first);
        u.EventInfo[eventId].LastDay=20251231;
        Check((await Get(u)).RemainTicket==1 && (await Get(u)).RemainTicket==1,"recharge once, even across years");
        var again=await Win(u,first);
        Check(again.FirstClearReward==null && u.Currency[(CurrencyType)5100]==1,"first reward repeated");
        u.EventInfo[eventId].LastDay=20251231;
        var next=await Win(u,second);
        Check(next.FirstClearReward.Item.Single(x=>x.Tid==9430003).Count==5 && u.Items.Single(x=>x.ItemType==9430003).Count==5,"mold not credited");
        Check((await Get(u)).LastClearedEventStageList.Single().StageId==second,"latest stage per difficulty");
    });
    await Test("quick battle requires a clear and settles the exact number of available tickets", async () => {
        var u=New(92015);
        await Reject(u,"fastclear",FastReq(1));
        await Win(u,first);
        u.Items.Add(new DbItemData{Isn=31,ItemType=ticket,Count=1});
        u.Items.Add(new DbItemData{Isn=32,ItemType=ticket,Count=2});
        foreach(var count in new[]{0,-1,4,int.MaxValue}) await Reject(u,"fastclear",FastReq(count));
        Check((await Get(u)).RemainTicket==3,"invalid batch consumed tickets");
        var s=ResFastClearChallengeEventStage.Parser.ParseFrom(await Ok(u,"fastclear",FastReq(3)));
        Check(s.RemainTicket==0 && s.Reward.Currency.Single(x=>x.Type==2000).Value==9660*3, "batch regular reward");
        Check(u.Items.Where(x=>x.ItemType==ticket).Sum(x=>x.Count)==0 && u.Currency[(CurrencyType)5100]==1,"batch tickets or first reward");
    });
    await Test("holding inventory tickets at reset preserves the balance without overcharging", async () => {
        var u=New(92016);
        await Win(u,first);
        u.Items.Add(new DbItemData{Isn=33,ItemType=ticket,Count=2});
        u.EventInfo[eventId].LastDay=20251231;
        Check((await Get(u)).RemainTicket==2,"paid tickets overwritten or an extra free attempt awarded");
    });
    await Test("concurrent settlements debit and pay once", async () => {
        var u=New(92017);
        u.Items.Add(new DbItemData{Isn=34,ItemType=ticket,Count=3});
        await Ok(u,"enter",EnterReq());
        await Task.WhenAll(Enumerable.Range(0,6).Select(_=>Send(u,"clear",ClearReq())));
        Check(u.Currency[(CurrencyType)2000]==28980 && (await Get(u)).RemainTicket==2,"double payout or debit");
    });
    await Test("missing reward configuration leaves the battle retryable", async () => {
        var u=New(92018);
        await Ok(u,"enter",EnterReq());
        var firstReward=data.RewardDataRecords[3500601];
        data.RewardDataRecords.Remove(firstReward.Id);
        try { Check((await Send(u,"clear",ClearReq())).Code==HttpStatusCode.InternalServerError,"expected configuration failure"); }
        finally {data.RewardDataRecords.Add(firstReward.Id,firstReward);}
        Check(u.Currency.GetValueOrDefault(CurrencyType.Gold)==0 && (await Get(u)).RemainTicket==1,"partial payout");
        await Ok(u,"clear",ClearReq());
    });
    await Test("legacy team and last-stage saves retain the correct squad and progress", async () => {
        var u = New(92019);
        u.UserTeams[20] = new NetUserTeamData { Type=20, LastContentsTeamNumber=4 };
        u.EventInfo[eventId] = new EventData { LastStage=second };
        var s = await Get(u);
        Check(s.TeamData.Type == 21 && s.TeamData.LastContentsTeamNumber == 4 && u.UserTeams[20].Type == 20, "legacy formation was not copied safely");
        Check(s.LastClearedEventStageList.Single().StageId == second && (await Win(u, first)).FirstClearReward == null, "lost legacy progress");
    });
    await Test("published activity start gates the next stage at the daily reset", async () => {
        var u = New(92020);
        var initial = clock.Now;
        try {
            clock.Now = info.ChallengeEventStartUtc[eventId];
            await Win(u, first);
            clock.Now = new DateTimeOffset(2026, 10, 31, 19, 59, 59, TimeSpan.Zero);
            Check((await Get(u)).RemainTicket == 1, "ticket available before opening");
            await Reject(u, "enter", EnterReq(second));
            clock.Now = clock.Now.AddSeconds(1);
            await Win(u, second);
            Check((await Get(u)).RemainTicket == 0, "reset must credit only once");
        } finally { clock.Now=initial; }
    });
    await Test("delayed replays cannot reopen a settled battle or consume a later entry", async () => {
        var u=New(92021);
        u.Items.Add(new DbItemData{Isn=44,ItemType=ticket,Count=4});
        await Ok(u,"enter",EnterReq(),"01");
        await Ok(u,"clear",ClearReq(),"02");
        u.Currency[CurrencyType.Gold]-=1000;
        await Ok(u,"enter",EnterReq(),"01");
        var replay=ResClearChallengeEventStage.Parser.ParseFrom(await Ok(u,"clear",ClearReq(),"02"));
        Check(replay.RemainTicket==3 && replay.Reward.Currency.Single(c=>c.Type==2000).FinalValue==27980, "replay returned stale credit");
        await Ok(u,"enter",EnterReq(),"03");
        Check((await Send(u,"clear",ClearReq(),"02")).Code==HttpStatusCode.Conflict, "stale clear consumed next entry");
        await Ok(u,"clear",ClearReq(),"04");
        Check((await Get(u)).RemainTicket==2 && u.Currency[(CurrencyType)5100]==1, "incorrect resettlement");
    });
    await Test("replaying an encrypted quick battle after a later one cannot pay again", async () => {
        var u=New(92022);
        u.Items.Add(new DbItemData{Isn=45,ItemType=ticket,Count=6});
        await Win(u,first);
        await Ok(u,"fastclear",FastReq(1),"A1");
        await Ok(u,"fastclear",FastReq(1),"B2");
        await Ok(u,"fastclear",FastReq(1),"B2");
        Check((await Send(u,"fastclear",FastReq(1),"A1")).Code==HttpStatusCode.Conflict, "accepted old batch");
        Check((await Get(u)).RemainTicket==3 && u.Currency[(CurrencyType)2000]==48300,"duplicate quick debit or credit");
    });
    await Test("settlement survives a failed mission database write and a save reload", async () => {
        var u=New(92023);
        await Ok(u,"enter",EnterReq());
        using var db=GameContext.CreateNew();
        db.Database.ExecuteSqlRaw("CREATE TRIGGER fail_stage BEFORE INSERT ON Triggers BEGIN SELECT RAISE(ABORT,'test interruption'); END;");
        try { Check((await Send(u,"clear",ClearReq())).Code==HttpStatusCode.InternalServerError,"expected interrupted delivery"); }
        finally { db.Database.ExecuteSqlRaw("DROP TRIGGER fail_stage;"); }
        var snapshot=File.ReadAllText(save);
        info=JsonConvert.DeserializeObject<CoreInfo>(snapshot)!;
        infoProperty.SetValue(null,info);
        u=info.Users.Single(x=>x.ID==92023);
        Check(u.EventInfo[eventId].Challenge!.PendingClear!=null && u.Currency[(CurrencyType)5100]==1,"reward and recovery record were not saved together");
        await Ok(u,"clear",ClearReq());
        // Simulate a restart between the SQL commit and clearing the notification in JSON.
        info=JsonConvert.DeserializeObject<CoreInfo>(snapshot)!;
        infoProperty.SetValue(null,info);
        u=info.Users.Single(x=>x.ID==92023);
        var s=await Get(u);
        Check(s.RemainTicket==0 && u.Currency[(CurrencyType)5100]==1 && u.EventInfo[eventId].Challenge!.PendingClear==null,"recovery changed payout");
        Check(db.Triggers.Count(t=>t.UserId==u.ID)==2,"duplicate or missing mission triggers");
    });
}
await app.StopAsync();
Console.WriteLine($"Challenge stage regression: {cases-failures}/{cases} passed");
return failures==0 ? 0 : 1;

async Task Load<T>(string name, Dictionary<int,T> dest, Func<T,int> id) where T:new() {
    foreach(var row in await data.LoadZip<T>(name+".json",new Progress<double>())) dest.Add(id(row),row);
}
User New(ulong id) {
    var u=new User { ID=id };
    info.Users.Add(u);
    using var db=GameContext.CreateNew(); db.Users.Add(new GameUser{ID=id}); db.SaveChanges();
    return u;
}
ReqEnterChallengeEventStage EnterReq(int stage=first) => new() {EventId=eventId,StageId=stage,TeamNumber=1};
ReqClearChallengeEventStage ClearReq(int stage=first,int result=1,int team=1) => new() {EventId=eventId,StageId=stage,TeamNumber=team,BattleResult=result};
ReqFastClearChallengeEventStage FastReq(int count) => new() {EventId=eventId,StageId=first,TeamNumber=1,ClearCount=count};
async Task<ResChallengeEventStageData> Get(User u) => ResChallengeEventStageData.Parser.ParseFrom(await Ok(u,"get",new ReqChallengeEventStageData{EventId=eventId}));
async Task<ResClearChallengeEventStage> Win(User u,int stage) {
    await Ok(u,"enter",EnterReq(stage));
    return ResClearChallengeEventStage.Parser.ParseFrom(await Ok(u,"clear",ClearReq(stage)));
}
async Task<(HttpStatusCode Code, byte[] Body)> Send(User u,string route,IMessage message,string? nonce=null) {
    using var request=new HttpRequestMessage(HttpMethod.Post,route);
    request.Headers.Add("Test-User",u.ID.ToString());
    if (nonce!=null) request.Headers.Add("Test-Nonce",nonce);
    request.Content=new ByteArrayContent(message.ToByteArray());
    request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream+protobuf");
    using var response=await client.SendAsync(request);
    return (response.StatusCode,await response.Content.ReadAsByteArrayAsync());
}
async Task<byte[]> Ok(User u,string route,IMessage message,string? nonce=null) {
    var (code,body)=await Send(u,route,message,nonce);
    Check(code==HttpStatusCode.OK,$"{route} returned {(int)code}: {System.Text.Encoding.UTF8.GetString(body).Split('<' ).First()}");
    return body;
}
async Task Reject(User u,string route,IMessage message) {
    var (code,_)=await Send(u,route,message);
    Check(code==HttpStatusCode.Conflict,$"{route} expected 409, got {(int)code}");
}

void Check(bool ok,string reason) { if(!ok) throw new Exception(reason); }
async Task Test(string name,Func<Task> action) {
    cases++;
    try {await action();Console.WriteLine($"PASS {name}");}
    catch(Exception e){failures++;Console.WriteLine($"FAIL {name}: {e.Message}");}
}

sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}
