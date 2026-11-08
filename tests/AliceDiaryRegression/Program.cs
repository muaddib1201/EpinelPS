using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.Models;
using EpinelPS.Utils;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

const ulong player = 1901, other = 1902;
var workspace = Environment.GetEnvironmentVariable("NIKKE_WORKSPACE_ROOT") ?? throw new Exception("Isolated workspace required.");
var data = new GameData(Directory.GetFiles(Path.Combine(workspace,"Server and Server selector/cache"),"StaticData.pack",SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
Console.WriteLine("STATIC Alice max-affinity messenger dependencies: "+data.MessageConditions.Values.Count(c=>c.TriggerList?.Any(t=>t.Trigger==Trigger.CharacterAttractiveLevelMax && t.ConditionId==5004)==true));
var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
var save = Path.Combine(AppContext.BaseDirectory,"db.json");
const string connection = "Data Source=alice-diary;Mode=Memory;Cache=Shared";
GameContext.InitializeOptions(connection,"sqlite");
using var sqlite = new SqliteConnection(connection); sqlite.Open();
using(var db = GameContext.CreateNew())
{
    db.Database.EnsureCreated();
    db.Users.AddRange(new GameUser { ID=player,Nickname="AliceFixture" },new GameUser { ID=other,Nickname="OtherFixture" });
    db.SaveChanges();
}
using var services = new ServiceCollection().AddDbContext<GameContext>(o=>o.UseSqlite(connection)).BuildServiceProvider();
int passed=0,failed=0;

await Run("saved 10/10/22 completes the actual skill and bond diary requirements",async()=>
{
    Alice().Skill1Lvl=Alice().Skill2Lvl=Alice().UltimateLevel=10; Bond().Lv=22;
    string before=JsonConvert.SerializeObject(State());
    var response=await Sync();
    Check(Total(response,133,0)==10 && Total(response,134,0)==10 && Total(response,76,5004)==22,"saved levels were not delivered to the diary");
    var missions=data.DailyEventTable.Values.Where(m=>m.EventId==20002 && (m.Trigger==Trigger.AliceSkill1Level || m.Trigger==Trigger.AliceSkillBurstLevel || (m.Trigger==Trigger.CharacterAttractiveLevelMax && m.ConditionId==5004))).ToArray();
    Check(missions.Length==15 && missions.All(m=>m.IsStack && m.IgnoreEventPeriod && Total(response,(int)m.Trigger,m.ConditionId)>=m.ConditionValue),"actual diary thresholds are not met by the sync payload");
    Check(JsonConvert.SerializeObject(State())==before,"repair changed saved characters, rewards or claimed missions");
});
await Run("partial history receives only its missing values and retains unrelated events",async()=>
{
    Alice().Skill1Lvl=Alice().UltimateLevel=10; Bond().Lv=22;
    Current().AddTrigger(Trigger.AliceSkill1Level,4);
    Current().AddTrigger(Trigger.AliceSkillBurstLevel,7);
    Current().AddTrigger(Trigger.CharacterAttractiveLevelMax,2,5004);
    Current().AddTrigger(Trigger.CharacterAttractiveLevelMax,40,5185);
    var response=await Sync();
    Check(response.Triggers.Any(t=>t.Trigger==133 && t.UserValue==6) && response.Triggers.Any(t=>t.Trigger==134 && t.UserValue==3) && response.Triggers.Any(t=>t.Trigger==76 && t.ConditionId==5004 && t.UserValue==20),"repair did not preserve cumulative history");
    Check(Total(response,133,0)==10 && Total(response,134,0)==10 && Total(response,76,5004)==22 && Total(response,76,5185)==40,"repair inflated or mixed up progress");
});
await Run("incremental sync and save reload do not pay or count the repair twice",async()=>
{
    Alice().Skill1Lvl=Alice().UltimateLevel=10; Bond().Lv=22;
    var first=await Sync();
    Check(first.Triggers.Count==3,"initial repair should publish exactly three notifications");
    jsonDb.GetMethod("Save")!.Invoke(null,null); jsonDb.GetMethod("Reload")!.Invoke(null,null);
    var second=await Sync(first.Triggers.Max(t=>t.Seq));
    Check(second.Triggers.Count==0 && (await Sync()).Triggers.Count==3,"restart or incremental sync duplicated progress");
});
await Run("simultaneous sync requests cannot duplicate the same recovery",async()=>
{
    Alice().Skill1Lvl=Alice().UltimateLevel=10; Bond().Lv=22;
    await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>Sync())));
    using var db=GameContext.CreateNew();
    Check(db.Triggers.Count(t=>t.UserId==player)==3,"parallel repair lost or duplicated notifications");
});
await Run("a reset and re-upgrade cannot count previously reported levels again",async()=>
{
    Alice().Skill1Lvl=Alice().UltimateLevel=10; Bond().Lv=22;
    await Sync();
    Alice().Skill1Lvl=Alice().UltimateLevel=1; Bond().Lv=2;
    await Sync();
    Alice().Skill1Lvl=Alice().UltimateLevel=10; Bond().Lv=23;
    var result=await Sync();
    Check(Total(result,133,0)==10 && Total(result,134,0)==10 && Total(result,76,5004)==23,"replayed levels inflated the diary");
});
await Run("an unowned Alice and other characters cannot fabricate Alice progress",async()=>
{
    Current().Characters.Clear(); Bond().Lv=22;
    var nonAlice=data.CharacterTable.Values.First(c=>c.NameCode!=5004);
    Current().Characters.Add(new CharacterModel { Csn=2,Tid=nonAlice.Id,Skill1Lvl=10,UltimateLevel=10 });
    Check((await Sync()).Triggers.Count==0,"unowned Alice received progress from a different character");
    Check((await Send<ResSyncTrigger>(other,"/trigger/sync",new ReqSyncTrigger())).Triggers.Count==0,"another account received Alice's progress");
});
foreach(bool legacy in new[]{true,false})
await Run($"{(legacy?"legacy":"V2")} skill upgrades update Alice skill 1 and burst on the next sync",async()=>
{
    await Sync();
    foreach(var category in new[]{CharacterSkillCategory.Skill1,CharacterSkillCategory.Ultimate})
    {
        if(legacy)
            for(int i=1;i<4;i++) await Send<ResCharacterSkillLevelUp>(player,"/character/skill/levelup",new ReqCharacterSkillLevelUp { Csn=1,Category=(int)category });
        else await Send<ResCharacterSkillLevelUpV2>(player,"/character/skill/levelupV2",new ReqCharacterSkillLevelUpV2 { Csn=1,SkillCategory=category,TargetLevel=4 });
    }
    var result=await Sync();
    Check(Total(result,133,0)==4 && Total(result,134,0)==4,"skill action was saved without diary progress");
});
foreach(string route in new[]{"normal","quick","gift"})
await Run($"{route} affinity gains count below the cap",async()=>
{
    Bond().Exp=data.AttractiveLevelTable.Values.Single(l=>l.AttractiveLevel==1).AttractivePoint-50;
    await Sync();
    if(route=="normal") await Send<ResCharacterCounsel>(player,"/character/attractive/counsel",new ReqCharacterCounsel { NameCode=5004 });
    else if(route=="quick") await Send<ResCharacterQuickCounsel>(player,"/character/counsel/quick",new ReqCharacterQuickCounsel { NameCode=5004 });
    else
    {
        var gift=data.itemMaterialTable.Values.First(m=>m.ItemSubType==ItemSubType.AttractiveMaterial && m.ItemValue>=50 && m.ItemValue<=100);
        await Send<ResCharacterPresent>(player,"/character/attractive/present",new ReqCharacterPresent { NameCode=5004,Items={new NetItemData { Tid=gift.Id,Count=1 }} });
    }
    Check(Bond().Lv==2 && Total(await Sync(),76,5004)==2,"sub-cap affinity did not update the diary");
});
await Run("reaching the affinity cap adds the remaining level exactly once",async()=>
{
    Bond().Lv=29;
    Bond().Exp=data.AttractiveLevelTable.Values.Single(l=>l.AttractiveLevel==29).AttractivePoint-50;
    await Sync();
    await Send<ResCharacterCounsel>(player,"/character/attractive/counsel",new ReqCharacterCounsel { NameCode=5004 });
    Check(Bond().Lv==30 && Total(await Sync(),76,5004)==30,"cap notification duplicated already reported affinity");
});
await Run("a database rejection leaves all repair values retryable without partial history",async()=>
{
    Alice().Skill1Lvl=Alice().UltimateLevel=10; Bond().Lv=22;
    using(var db=GameContext.CreateNew()) db.Database.ExecuteSqlRaw("CREATE TRIGGER reject_alice BEFORE INSERT ON Triggers WHEN NEW.Type = 134 BEGIN SELECT RAISE(FAIL, 'temporary diary delivery failure'); END;");
    bool rejected=false;
    try { await Sync(); } catch(DbUpdateException) { rejected=true; }
    finally { using var db=GameContext.CreateNew(); db.Database.ExecuteSqlRaw("DROP TRIGGER reject_alice;"); }
    using(var db=GameContext.CreateNew()) Check(rejected && !db.Triggers.Any(t=>t.UserId==player),"failed repair committed partial progress");
    var result=await Sync();
    Check(Total(result,133,0)==10 && Total(result,134,0)==10 && Total(result,76,5004)==22,"retry lost or duplicated repair values");
});

Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed==0?0:1;

CoreInfo State()=>(CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
User Current()=>State().Users.Single(u=>u.ID==player);
CharacterModel Alice()=>Current().Characters.Single(c=>c.Csn==1);
NetUserAttractiveData Bond()=>Current().BondInfo.Single(b=>b.NameCode==5004);
static long Total(ResSyncTrigger r,int trigger,int condition)=>r.Triggers.Where(t=>t.Trigger==trigger && t.ConditionId==condition).Sum(t=>t.UserValue);
Task<ResSyncTrigger> Sync(long seq=0)=>Send<ResSyncTrigger>(player,"/trigger/sync",new ReqSyncTrigger { Seq=seq });
void Reset()
{
    var user=new User { ID=player,LastReset=DateTime.UtcNow,LastWeeklyReset=DateTime.UtcNow,TriggerSyncClockVersion=1 };
    user.Characters.Add(new CharacterModel { Csn=1,Tid=319110,Grade=9,Skill1Lvl=1,Skill2Lvl=1,UltimateLevel=1 });
    user.BondInfo.Add(new NetUserAttractiveData { NameCode=5004,Lv=1,CanCounselToday=true });
    foreach(CurrencyType currency in Enum.GetValues<CurrencyType>()) user.Currency[currency]=1_000_000_000;
    user.Items.AddRange(data.itemMaterialTable.Keys.Select(id=>new DbItemData { ItemType=id,Count=1_000_000 }));
    user.EventMissionInfo[20002]=new EventMissionData { MissionIdList=[200020206],LastDay=user.GetDateDay() };
    File.WriteAllText(save,JsonConvert.SerializeObject(new CoreInfo { DbVersion=5,BondRewardLevelsMigrated=true,LauncherTokenKey=new byte[32],EncryptionTokenKey=new byte[32],Users=[user,new User { ID=other,TriggerSyncClockVersion=1 }] }));
    jsonDb.GetMethod("Reload")!.Invoke(null,null);
    using var db=GameContext.CreateNew(); db.Triggers.RemoveRange(db.Triggers); db.SaveChanges();
}
async Task<T> Send<T>(ulong id,string route,IMessage request) where T:IMessage<T>,new()
{
    using var scope=services.CreateScope();
    var ctx=new DefaultHttpContext { RequestServices=scope.ServiceProvider };
    ctx.Items["UserID"]=id; ctx.Request.Path="/v1"+route;
    ctx.Request.Body=new MemoryStream(request.ToByteArray()); ctx.Response.Body=new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    return (T)new T().Descriptor.Parser.ParseFrom(((MemoryStream)ctx.Response.Body).ToArray());
}
async Task Run(string name,Func<Task> body)
{
    try { Reset(); await body(); passed++; Console.WriteLine("PASS "+name); }
    catch(Exception ex) { failed++; Console.WriteLine("FAIL "+name+": "+ex.Message); }
}
static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
