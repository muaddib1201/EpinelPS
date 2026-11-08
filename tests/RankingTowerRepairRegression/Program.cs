using System.Diagnostics;
using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

const ulong self=9911;
var root=new DirectoryInfo(AppContext.BaseDirectory);
while(root!=null&&!Directory.Exists(Path.Combine(root.FullName,"Server and Server selector")))root=root.Parent;
var data=new GameData(Directory.GetFiles(Path.Combine(root!.FullName,"Server and Server selector/cache"),"StaticData.pack",SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
string save=Path.Combine(AppContext.BaseDirectory,"db.json"),sqlite=Path.Combine(AppContext.BaseDirectory,"repair.db");
var instance=typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
GameContext.InitializeOptions($"Data Source={sqlite}","sqlite");
var corps=new[]{CorporationTowerType.ELYSION,CorporationTowerType.MISSILIS,CorporationTowerType.TETRA,CorporationTowerType.OVERSPEC};
var triggers=new[]{Trigger.TowerElysionClear,Trigger.TowerMissilisClear,Trigger.TowerTetraClear,Trigger.TowerOverspecClear};
var originals=data.towerTable.ToDictionary();var targets=data.Rankings.ToDictionary();
int passed=0,failed=0;
if(args.Contains("--verify-saved"))
{
    Execute();Check(Me().GetCurrencyVal(CurrencyType.FreeCash)==192&&Me().Items.Where(i=>i.ItemType==9201001).Sum(i=>i.Count)==288,"restart duplicated or lost floor rewards");
    Check(corps.All(c=>Me().TowerProgress[c]==27)&&Me().TowerProgress[CorporationTowerType.ALL]==95,"restart changed tower progress");
    Console.WriteLine("PASS repair rewards and progress survive a fresh process");return 0;
}
File.WriteAllText(save,JsonConvert.SerializeObject(Fixture()));
using(var db=GameContext.CreateNew()){db.Database.EnsureDeleted();db.Database.EnsureCreated();db.Users.AddRange(Enumerable.Range(9911,11).Select(i=>new GameUser{ID=(ulong)i,Nickname=i==9911?"ZOLRATH":"Repair"+i}));db.SaveChanges();}

await Test("first transfer swaps reserved places while preserving receipts and historical score",()=>
{
    Me().Ranking=new(){Achievements={[10201]=new(){Rank=3,Score=900,AchievedAt=300}},LegacyCounts={[31]=4},ClaimedServerTargets=[10201],MailedCumulativeRewards=[60001]};
    Other().Ranking=new(){Achievements={[10201]=new(){Rank=1,Score=600,AchievedAt=100}}};
    Transfer();
    Check(Me().Ranking!.Achievements[10201] is {Rank:1,Score:900,AchievedAt:100},"target account did not receive first record");
    Check(Other().Ranking!.Achievements[10201] is {Rank:3,Score:600,AchievedAt:100},"former first lost its score or place");
    Check(Me().Ranking!.LegacyCounts[31]==4&&Me().Ranking.ClaimedServerTargets.SetEquals([10201])&&Me().Ranking.MailedCumulativeRewards.SetEquals([60001]),"transfer reset reward receipts");return Task.CompletedTask;
});
await Test("inserting an absent account keeps ten unique places and no future target",()=>
{
    for(int n=0;n<10;n++)State().Users[n+1].Ranking=new(){Achievements={[10201]=new(){Rank=n+1,Score=600,AchievedAt=100+n}}};
    Transfer();var records=State().Users.Where(u=>u.Ranking?.Achievements.ContainsKey(10201)==true).Select(u=>u.Ranking!.Achievements[10201]).ToArray();
    Check(records.Length==10&&records.Select(a=>a.Rank).Order().SequenceEqual(Enumerable.Range(1,10)),"transfer duplicated or exceeded reserved positions");
    Check(Me().Ranking!.Achievements[10201].Rank==1&&Other().Ranking!.Achievements[10201].Rank==2,"old winner did not move behind inserted account");
    Check(State().Users.All(u=>u.Ranking?.Achievements.ContainsKey(10202)!=true),"transfer unlocked an unreached target");return Task.CompletedTask;
});
await Test("repeating first transfer changes neither history nor receipts",()=>
{
    Me().Ranking=new(){Achievements={[10201]=new(){Rank=2,Score=900,AchievedAt=300}}};Other().Ranking=new(){Achievements={[10201]=new(){Rank=1,Score=600,AchievedAt=100}}};
    Transfer();string before=JsonConvert.SerializeObject(State());Transfer();Check(JsonConvert.SerializeObject(State())==before,"repeating transfer mutated history");return Task.CompletedTask;
});
await Test("four tower advances use normal rewards once and survive process restart",async()=>
{
    SyntheticRewards();Execute();
    Check(Me().GetCurrencyVal(CurrencyType.FreeCash)==192,"96 missing floors must award exactly 192 fixture gems");
    Check(Me().Items.Where(i=>i.ItemType==9201001).Sum(i=>i.Count)==288,"normal item reward writer was bypassed");
    Check(corps.All(c=>Me().TowerProgress[c]==27&&Me().ResetableData.TowerCount[c]==27)&&Me().TowerProgress[CorporationTowerType.ALL]==95,"incorrect progress or clear count");
    using(var db=GameContext.CreateNew())Check(db.Triggers.Count(t=>t.UserId==self&&triggers.Contains(t.Type))==108,"each manufacturer floor must have one clear receipt");
    var before=JsonConvert.SerializeObject(Me());Execute();Check(JsonConvert.SerializeObject(Me())==before,"retry changed rewards, milestones or mail");
    using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--verify-saved"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
    var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();Check(child.ExitCode==0,(await output)+(await error));
});
await Test("durable and pending higher clears prevent duplicate rewards and ignore foreign history",()=>
{
    SyntheticRewards();Record(self,Trigger.TowerElysionClear,20007);Me().PendingMissionTriggers.Add(new(){UserId=self,Type=Trigger.TowerTetraClear,ConditionId=40008,Value=1,CreatedAt=123});
    Record(9912,Trigger.TowerElysionClear,20026);Record(self,Trigger.TowerMissilisClear,20026);
    Execute();Check(Me().GetCurrencyVal(CurrencyType.FreeCash)==174,"history reconciliation counted a foreign category or rewarded proven clears again");return Task.CompletedTask;
});
await Test("missing last tower reward rejects the entire plan before any mutation",()=>
{
    SyntheticRewards();var floor=data.towerTable.Values.Single(t=>t.Type==CorporationTowerType.OVERSPEC&&t.Floor==27);data.towerTable[floor.Id]=Clone(floor);data.towerTable[floor.Id].RewardId=int.MaxValue;
    string before=JsonConvert.SerializeObject(State());ExpectFailure(()=>Execute());Check(JsonConvert.SerializeObject(State())==before,"failed preflight partially changed the account");return Task.CompletedTask;
});
await Test("unknown account and invalid target floor cannot mutate state",()=>
{
    string before=JsonConvert.SerializeObject(State());ExpectFailure(()=>Invoke("Execute",999999UL,27));ExpectFailure(()=>Invoke("Execute",self,0));
    Check(JsonConvert.SerializeObject(State())==before,"invalid repair request mutated state");return Task.CompletedTask;
});
await Test("a reward writer failure rolls back every tower inventory and daily counter",()=>
{
    SyntheticRewards();var row=data.towerTable.Values.Single(t=>t.Type==CorporationTowerType.OVERSPEC&&t.Floor==27);
    data.towerTable[row.Id]=Clone(row);data.towerTable[row.Id].RewardId=910000002;
    data.RewardDataRecords[910000002]=new(){Id=910000002,Rewards=[new(){RewardType=RewardType.Bgm,RewardId=int.MaxValue,RewardValue=1,RewardPercent=1000000}]};
    string before=JsonConvert.SerializeObject(State());ExpectFailure(()=>Execute());Check(JsonConvert.SerializeObject(State())==before,"reward failure retained partial inventory or progress");return Task.CompletedTask;
});
await Test("duplicate historical places reject before floor rewards",()=>
{
    SyntheticRewards();Me().Ranking=new(){Achievements={[10201]=new(){Rank=1}}};Other().Ranking=new(){Achievements={[10201]=new(){Rank=1}}};
    string before=JsonConvert.SerializeObject(State());ExpectFailure(()=>Execute());Check(JsonConvert.SerializeObject(State())==before,"invalid rank records allowed partial repair");return Task.CompletedTask;
});
await Test("transfer credits only newly gained cumulative tiers and never repeats",()=>
{
    SyntheticRewards();data.Rankings.Clear();data.Rankings[10201]=new(){Id=10201,RankingType=RankingType.GroupPointElysion,ConditionValue=300};
    Me().Ranking=new(){Achievements={[10201]=new(){Rank=7,Score=900,AchievedAt=300}},ClaimedServerTargets=[10201]};Other().Ranking=new(){Achievements={[10201]=new(){Rank=1,Score=600,AchievedAt=100}}};
    Execute();
    using var db=GameContext.CreateNew();Check(db.Triggers.Where(t=>t.UserId==self&&t.Type==Trigger.AchieveRanking1st).Sum(t=>t.Value)==1&&db.Triggers.Where(t=>t.UserId==self&&t.Type==Trigger.AchieveRanking5th).Sum(t=>t.Value)==1&&!db.Triggers.Any(t=>t.UserId==self&&t.Type==Trigger.AchieveRanking10th),"cumulative events duplicated an already earned tier");
    int count=db.Triggers.Count();int mails=Me().MailDatas.Count;Execute();Check(db.Triggers.Count()==count&&Me().MailDatas.Count==mails&&Me().Ranking!.ClaimedServerTargets.SetEquals([10201]),"retry repeated cumulative rewards or erased a gift receipt");return Task.CompletedTask;
});
await Test("maintenance CLI rejects a data root outside a private worktree before loading saves",async()=>
{
    string tool=Path.Combine(root!.FullName,"src/EpinelPS/tools/RankingTowerRepair/bin/Release/net10.0/win-x64/RankingTowerRepair.exe");
    var start=new ProcessStartInfo(tool){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(string arg in new[]{"--workspace",Path.GetTempPath(),"--account",self.ToString(),"--floor","27","--report",Path.Combine(AppContext.BaseDirectory,"rejected.json")})start.ArgumentList.Add(arg);
    using var child=Process.Start(start)!;var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();
    Check(child.ExitCode!=0&&(await error).Contains("private worktree")&&!File.Exists(Path.Combine(AppContext.BaseDirectory,"rejected.json")),"CLI accepted an unisolated data path");await output;
});
await Test("first transfer outbox survives a failed second tier delivery without duplicate counters",()=>
{
    data.Rankings.Clear();data.Rankings[10201]=new(){Id=10201,RankingType=RankingType.GroupPointElysion,ConditionValue=300};
    foreach(var company in corps)Me().TowerProgress[company]=27;
    Me().Ranking=new(){Achievements={[10201]=new(){Rank=7,Score=900,AchievedAt=300}}};Other().Ranking=new(){Achievements={[10201]=new(){Rank=1,Score=600,AchievedAt=100}}};
    using var db=GameContext.CreateNew();db.Database.ExecuteSqlRaw("CREATE TRIGGER repair_fail_second_tier BEFORE INSERT ON Triggers WHEN NEW.Type=32 BEGIN SELECT RAISE(ABORT,'injected tier delivery failure'); END");
    try{Execute();Check(Me().Ranking!.Achievements[10201].Rank==1&&Me().PendingMissionTriggers.Count==2,"first assignment and durable outbox were not committed together");}
    finally{db.Database.ExecuteSqlRaw("DROP TRIGGER repair_fail_second_tier");}
    instance.SetValue(null,JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))!);Execute();
    Check(Me().PendingMissionTriggers.Count==0&&db.Triggers.Where(t=>t.UserId==self&&t.Type==Trigger.AchieveRanking1st).Sum(t=>t.Value)==1&&db.Triggers.Where(t=>t.UserId==self&&t.Type==Trigger.AchieveRanking5th).Sum(t=>t.Value)==1,"outbox recovery repeated the first tier");return Task.CompletedTask;
});
Console.WriteLine($"Ranking tower repair regression: {passed} passed, {failed} failed");return failed==0?0:1;

CoreInfo State()=>(CoreInfo)instance.GetValue(null)!;
User Me()=>State().Users[0];User Other()=>State().Users[1];
CoreInfo Fixture()=>new(){DbVersion=5,BondRewardLevelsMigrated=true,LauncherTokenKey=Enumerable.Repeat((byte)71,32).ToArray(),EncryptionTokenKey=Enumerable.Repeat((byte)71,32).ToArray(),Users=Enumerable.Range(9911,11).Select(i=>new User{ID=(ulong)i,LastReset=DateTime.UtcNow,LastWeeklyReset=DateTime.UtcNow}).ToList()};
void Transfer()=>Invoke("TransferFirsts",State(),self);
void Execute()=>Invoke("Execute",self,27);
object? Invoke(string method,params object[] values)
{
    var type=typeof(User).Assembly.GetType("EpinelPS.Services.RankingTowerRepair")??throw new InvalidOperationException("Ranking tower maintenance operation is unavailable");
    try{return type.GetMethod(method,BindingFlags.Public|BindingFlags.Static)!.Invoke(null,values);}catch(TargetInvocationException e){throw e.InnerException!;}
}
async Task Test(string name,Func<Task> action)
{
    try
    {
        instance.SetValue(null,Fixture());using(var db=GameContext.CreateNew())db.Triggers.ExecuteDelete();
        data.towerTable.Clear();foreach(var p in originals)data.towerTable.Add(p.Key,p.Value);data.Rankings.Clear();foreach(var p in targets)data.Rankings.Add(p.Key,p.Value);
        Me().TowerProgress[CorporationTowerType.ALL]=95;
        for(int n=0;n<corps.Length;n++){Me().TowerProgress[corps[n]]=3;Me().ResetableData.TowerCount[corps[n]]=3;for(int floor=1;floor<=3;floor++)Record(self,triggers[n],(n+2)*10000+floor);}
        await action();passed++;Console.WriteLine("PASS "+name);
    }catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.Message);}
}
void Record(ulong id,Trigger trigger,int floor){using var db=GameContext.CreateNew();db.Triggers.Add(new(){UserId=id,Type=trigger,ConditionId=floor,Value=1});db.SaveChanges();}
TowerRecord Clone(TowerRecord row)=>JsonConvert.DeserializeObject<TowerRecord>(JsonConvert.SerializeObject(row))!;
void SyntheticRewards()
{
    data.RewardDataRecords[910000001]=new(){Id=910000001,Rewards=[new(){RewardType=RewardType.Currency,RewardId=(int)CurrencyType.FreeCash,RewardValue=2,RewardPercent=1000000},new(){RewardType=RewardType.Item,RewardId=9201001,RewardValue=3,RewardPercent=1000000}]};
    foreach(var p in originals.Where(p=>corps.Contains(p.Value.Type)&&p.Value.Floor is >=4 and <=27)){var copy=Clone(p.Value);copy.RewardId=910000001;data.towerTable[p.Key]=copy;}
}
static void ExpectFailure(Action action){try{action();}catch(Exception e)when(e.Message!="Ranking tower maintenance operation is unavailable"){return;}throw new Exception("invalid repair request was accepted");}
static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
