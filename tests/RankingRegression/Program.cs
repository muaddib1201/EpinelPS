using System.Diagnostics;
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
using Paseto;
using Paseto.Builder;
if(args.Contains("--probe-live"))return await RankingLiveProbe.Run();
const ulong self=9801;
var root=new DirectoryInfo(AppContext.BaseDirectory);
while(root!=null&&!Directory.Exists(Path.Combine(root.FullName,"Server and Server selector")))root=root.Parent;
var data=new GameData(Directory.GetFiles(Path.Combine(root!.FullName,"Server and Server selector/cache"),"StaticData.pack",SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var rawTargets=await data.LoadZip<RankingRecord>("RankingTable.json",new Progress<double>());
var rawMuseumMissions=data.MuseumMissionTable.ToArray();
if(args.Contains("--inspect-tables"))
{
    Console.WriteLine(JsonConvert.SerializeObject(new { targets = rawTargets.GroupBy(t=>t.RankingType).Select(g=>new { type=g.Key.ToString(),count=g.Count(),first=g.Take(2) }), groups=data.MuseumGroupTable.Values, missions=data.MuseumMissionTable.Values.Take(3), towers=data.towerTable.Values.GroupBy(t=>t.Type).Select(g=>new { type=g.Key.ToString(),first=g.OrderBy(t=>t.Floor).First() }) },Formatting.Indented));
    return 0;
}
string jsonPath=Path.Combine(AppContext.BaseDirectory,"db.json"),conn=$"Data Source={Path.Combine(AppContext.BaseDirectory,"ranking.db")}";
GameContext.InitializeOptions(conn,"sqlite");
byte[] key=Enumerable.Repeat((byte)77,32).ToArray();bool restart=args.Contains("--verify-saved")||args.Contains("--verify-completion-saved");
if(!restart){File.WriteAllText(jsonPath,JsonConvert.SerializeObject(Fixture()));using var db=GameContext.CreateNew();db.Database.EnsureDeleted();db.Database.EnsureCreated();db.Users.AddRange(Enumerable.Range(9801,13).Select(i=>new GameUser{ID=(ulong)i,Nickname="Rank"+i}));db.SaveChanges();}
var property=typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
CoreInfo State()=>(CoreInfo)property.GetValue(null)!;
using var services=new ServiceCollection().AddDbContext<GameContext>(o=>o.UseSqlite(conn)).BuildServiceProvider();
int passed=0,failed=0;
if(args.Contains("--verify-completion-saved"))
{
    CompletionTargets();
    Check((await Board(self,5)).User.Score==30&&(await MuseumTotal(self)).User.Score==10,"restart lost tower or museum points");
    await AllClaim(self);
    Check(ItemCount()==2&&(await Achievements(self)).TriggerList.All(t=>t.UserValue==2),"restart duplicated new ranking rewards or milestones");
    Console.WriteLine("PASS fresh process preserves tower and museum rankings and reward receipts");return 0;
}
if(restart){Targets(true);var top=await Achievements(self);Check(top.TriggerList.Single(x=>x.Trigger==31).UserValue==10,"restart lost counts");await AllClaim(self);await Achievements(self);Check(ItemCount()==10&&Player(self).MailDatas.Count==3,"restart reissued rewards");Console.WriteLine("PASS fresh process preserves ranking receipts");return 0;}
await Test("overview returns all ten campaign corporation and tower cards",async()=>{Player(self).LastHardStageCleared=7001001;for(int c=1;c<=4;c++)Add(self,c,1);foreach(var type in Enum.GetValues<CorporationTowerType>())Player(self).TowerProgress[type]=1;var r=await Send<ResGetAllTopRank>(self,"alltoprank",new ReqGetAllTopRank());Check(r.TopRankingList.Select(x=>x.Type).Order().SequenceEqual(Enumerable.Range(0,10)),"tower cards missing from overview");Check(r.TopRankingList.All(x=>x.User?.Usn==(long)self&&x.User.Nickname=="Rank9801"&&x.User.UserTitleDisplayData!=null),"public profile missing");});
await Test("company points combine native level with rarity breakthrough and core",async()=>{Add(self,1,1);Add(9802,1,11);Player(self).Characters[0].Level=999;var r=await Board(self,2);Check(r.User.Score==1299&&r.User.Rank==2&&r.RankingList[0].Score==3301,"wrong growth points or ranking");Check((await Board(self,1)).User.Score==0,"company mapping swapped");});
await Test("duplicate variants contribute the strongest NameCode once",async()=>{Add(self,1,1);Add(self,1,11);Check((await Board(self,2)).User.Score==3301,"duplicate variants counted");});
await Test("four company mappings exclude abnormal characters",async()=>{for(int c=1;c<=4;c++)Add(self,c,1);Add(self,7,11);foreach(int t in new[]{1,2,3,4})Check((await Board(self,t)).User.Score==301,"wrong corporation mapping");});
await Test("hard board rejects normal or nonexistent progress",async()=>{Player(self).LastHardStageCleared=6007006;Check((await Board(self,0)).User.Score==0,"normal stage ranked");Player(self).LastHardStageCleared=7001001;Check((await Board(self,0)).User.Score==7001001,"hard stage missing");Player(self).LastHardStageCleared=int.MaxValue;Check((await Board(self,0)).User.Score==0,"invalid stage ranked");});
await Test("stable ties banned exclusion and zero personal profile",async()=>{Add(9802,1,1);Add(9803,1,1);Add(9804,1,11);Player(9804).IsBanned=true;var r=await Board(self,2);Check(r.RankingList.Select(x=>x.User.Usn).SequenceEqual(new long[]{9802,9803}),"tie order or banned filtering");Check(r.User is{Rank:0,Score:0,User.Usn:(long)self},"personal row missing");});
await Test("milestone winners are retained across later progress and refresh",async()=>{Targets(true);Add(9802,1,11);await Achievements(self);Add(self,1,11);var r=await Achievements(self);Check(r.TriggerList.Single(x=>x.Trigger==31).UserValue==0&&r.TriggerList.Single(x=>x.Trigger==32).UserValue==10,"historical winner displaced");string before=JsonConvert.SerializeObject(State());await Achievements(self);await Board(self,2);Check(JsonConvert.SerializeObject(State())==before,"refresh recounted targets");var leaders=await Send<ResGetRankAchievementLeaderBoard>(self,"rankachievement",new ReqGetRankAchievementLeaderBoard{Type=2});Check(leaders.RankAchievement.Count==20&&leaders.RankAchievement.Count(x=>x.User.Usn==9802&&x.Rank==1)==10&&leaders.RankAchievement.All(x=>x.AchievedAt>0),"milestone leaderboard incomplete");});
await Test("first top-five and top-ten counters respect boundaries",async()=>{Targets(true);foreach(var p in State().Users)Add(p.ID,1,11);var a=await Achievements(self);var b=await Achievements(9806);var c=await Achievements(9811);Check(a.TriggerList.All(x=>x.UserValue==10),"first place counters wrong");Check(b.TriggerList.Single(x=>x.Trigger==32).UserValue==0&&b.TriggerList.Single(x=>x.Trigger==33).UserValue==10,"sixth place wrong");Check(c.TriggerList.All(x=>x.UserValue==0),"eleventh gained credit");});
await Test("hard chapter targets require the final main stage",async()=>{var stages=data.StageDataRecords.Values.Where(s=>s.ChapterMod==ChapterMod.Hard&&s.StageType==StageType.Main).GroupBy(s=>s.ChapterId).First(g=>g.Count()>1);var target=rawTargets.Single(t=>t.RankingType==RankingType.HardCampaign&&t.ConditionId==stages.Key);var table=RankingTable();table.Clear();table[target.Id]=target;Player(self).LastHardStageCleared=stages.Min(s=>s.Id);Check((await Achievements(self)).TriggerList.All(x=>x.UserValue==0),"chapter target awarded early");Player(self).LastHardStageCleared=stages.Max(s=>s.Id);Check((await Achievements(self)).TriggerList.All(x=>x.UserValue==1),"chapter target missing");});
await Test("unranked users can claim unlocked server rewards once per target",async()=>{Targets(true);Add(9802,1,11);Check((await Send<ResUpdateRankingServerReward>(self,"updateserverreward",new ReqUpdateRankingServerReward())).HasNewReward,"reward flag missing");Check((await Board(self,2)).RewardEnabled,"board reward flag missing");await Send<ResObtainRankAchievementServerReward>(self,"rankachievementserverreward",new ReqObtainRankAchievementServerReward{Type=2});Check(ItemCount()==10,"ten server gifts missing");await AllClaim(self);Check(ItemCount()==10&&!(await Send<ResUpdateRankingServerReward>(self,"updateserverreward",new ReqUpdateRankingServerReward())).HasNewReward,"rewards duplicated");});
await Test("parallel reward retries pay once",async()=>{Targets(true);Add(self,1,11);await Achievements(self);await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>AllClaim(self))));Check(ItemCount()==10,"parallel duplicate reward");});
await Test("ranking frames are mailed automatically once",async()=>{Targets(true);Add(self,1,11);await Achievements(self);var mails=Player(self).MailDatas.Values.ToArray();Check(mails.Length==3&&mails.All(m=>m.HasReward&&m.State==1&&m.Items.Count==1&&m.Items[0].RewardType==(int)RewardType.Frame),"three frame mails missing");await Achievements(self);Check(Player(self).MailDatas.Count==3,"mail duplicated");});
await Test("ordinary achievement route cannot bypass ranking requirements",async()=>{try{await Send<ResObtainAchievementReward>(self,"/mission/obtain/achievement",new ReqObtainAchievementReward{TidList={60001}});throw new Exception("bypass accepted");}catch(BadHttpRequestException){}Check(Player(self).FrameList.Count==0,"unearned frame");});
await Test("failed settlement save rolls back winners mail and SQLite events",async()=>{Targets(true);Add(self,1,11);string before=JsonConvert.SerializeObject(State());Directory.CreateDirectory(jsonPath+".tmp");try{try{await Achievements(self);throw new Exception("failure ignored");}catch(IOException){}catch(UnauthorizedAccessException){}}finally{Directory.Delete(jsonPath+".tmp");}using var db=GameContext.CreateNew();Check(JsonConvert.SerializeObject(State())==before&&!db.Triggers.Any(),"partial settlement persisted");});
await Test("failed claim save rolls back inventory and receipts",async()=>{Targets(true);Add(self,1,11);await Achievements(self);string before=JsonConvert.SerializeObject(Player(self));Directory.CreateDirectory(jsonPath+".tmp");try{try{await AllClaim(self);throw new Exception("failure ignored");}catch(IOException){}catch(UnauthorizedAccessException){}}finally{Directory.Delete(jsonPath+".tmp");}Check(JsonConvert.SerializeObject(Player(self))==before,"partial claim persisted");await AllClaim(self);Check(ItemCount()==10,"retry after rollback failed");});
await Test("invalid types and anonymous requests cannot mutate state",async()=>{string before=JsonConvert.SerializeObject(State());try{await Board(self,999);throw new Exception("invalid accepted");}catch(BadHttpRequestException){}try{await Board(0,2);throw new Exception("anonymous accepted");}catch(UnauthorizedAccessException){}Check(JsonConvert.SerializeObject(State())==before,"invalid request mutated save");});
await Test("fresh process preserves all counts and claim receipts",async()=>{Targets(true);Add(self,1,11);await Achievements(self);await AllClaim(self);using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--verify-saved"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();Check(child.ExitCode==0,(await output)+(await error));});
await Test("daily mission route cannot bypass ranking rewards",async()=>{try{await Send<ResObtainDailyMissionReward>(self,"/mission/obtain/daily",new ReqObtainDailyMissionReward{TidList={60001}});throw new Exception("daily bypass accepted");}catch(BadHttpRequestException){}Check(Player(self).FrameList.Count==0,"daily route granted unearned ranking frame");});
await Test("actual character upgrades observe first crossing before a later higher score",async()=>{Targets(true);var table=RankingTable();table.Clear();table[10201]=new(){Id=10201,RankingType=RankingType.GroupPointElysion,ConditionValue=600,ServerRewardId=15000001};Add(self,1,1);Add(9802,1,1);foreach(var id in new ulong[]{self,9802}){var c=data.CharacterTable[Player(id).Characters[0].Tid];Player(id).Items.Add(new(){Isn=1,ItemType=c.PieceId,Count=3});}await Achievements(self);await Send<ResCharacterUpgrade>(self,"/character/upgrade",new ReqCharacterUpgrade{Csn=1,Isn=1,Count=1});await Send<ResCharacterUpgrade>(9802,"/character/upgrade",new ReqCharacterUpgrade{Csn=1,Isn=1,Count=2});var ranks=await Send<ResGetRankAchievementLeaderBoard>(self,"rankachievement",new ReqGetRankAchievementLeaderBoard{Type=2});Check(ranks.RankAchievement.Single(r=>r.Rank==1).User.Usn==(long)self,"later higher score stole first crossing");});
await Test("NPC snapshot replacement cannot discard reserved milestone winners",async()=>{Targets(true);Add(self,1,1);Add(9802,1,11);Player(9802).ManagedNpc=new();var gate=typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetField("SaveLock",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;Exception? error=null;Thread worker;lock(Player(9813)){worker=new Thread(()=>{try{Board(self,2).GetAwaiter().GetResult();}catch(Exception ex){error=ex;}});worker.Start();Check(SpinWait.SpinUntil(()=>worker.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin),10000),"worker did not reach blocked snapshot");lock(gate)EpinelPS.Services.ManagedNpcService.Configure(false,1,DateTime.UtcNow);}Check(worker.Join(20000),"ranking snapshot retry deadlocked");if(error!=null)throw error;var current=Player(9802);var rank=Newtonsoft.Json.Linq.JObject.Parse(JsonConvert.SerializeObject(current))["Ranking"]?["Achievements"];Check(rank?.Count()==10,"NPC winner was written to detached object");await Task.CompletedTask;});
await Test("rank frames stay locked until automatic reward mail is claimed",async()=>{Targets(true);Add(self,1,11);await Achievements(self);var frameIds=Player(self).MailDatas.Values.SelectMany(m=>m.Items).Select(i=>i.RewardId).ToHashSet();var before=await Send<ResGetProfileFrame>(self,"/User/GetProfileFrame",new ReqGetProfileFrame());Check(!before.Frames.Any(frameIds.Contains),"unclaimed ranking frames are already advertised as owned");var claim=await Send<ResAllObtainMail>(self,"/mail/allobtain",new ReqAllObtainMail());Check(claim.Reward.ProfileFrame.Count==3&&frameIds.All(Player(self).FrameList.Contains),"mail did not grant owned rank frames");var after=await Send<ResGetProfileFrame>(self,"/User/GetProfileFrame",new ReqGetProfileFrame());Check(frameIds.All(after.Frames.Contains),"earned frame missing from client catalogue");await Send<ResAllObtainMail>(self,"/mail/allobtain",new ReqAllObtainMail());Check(Player(self).FrameList.Count==3,"mail retry duplicated frame ownership");});
await Test("five tower boards return literal floors in overview detail and personal rows",async()=>
{
    var types=new[]{CorporationTowerType.ALL,CorporationTowerType.MISSILIS,CorporationTowerType.ELYSION,CorporationTowerType.TETRA,CorporationTowerType.OVERSPEC};
    var floors=new[]{29,30,40,50,60};
    for(int n=0;n<types.Length;n++){Player(self).TowerProgress[types[n]]=floors[n];var board=await Board(self,n+5);Check(board.User is{Rank:1}&&board.User.Score==floors[n]&&board.RankingList.Single().Score==floors[n],"tower score must be the cleared floor, not the stage ID");}
    var overview=await Send<ResGetAllTopRank>(self,"alltoprank",new ReqGetAllTopRank());
    for(int n=0;n<types.Length;n++)Check(overview.TopRankingList.Single(r=>r.Type==n+5).Score==floors[n],"overview returned an internal tower ID");
    Player(self).TowerProgress[CorporationTowerType.ALL]=int.MaxValue;
    Check((await Board(self,5)).User.Score==0,"nonexistent tower floor ranked");
});
await Test("legacy SQLite tower clears belong only to their user and category",async()=>
{
    using(var db=GameContext.CreateNew()){db.Triggers.AddRange(new TriggerModelNew{UserId=self,Type=Trigger.TowerBasicClear,ConditionId=10030,Value=1},new TriggerModelNew{UserId=9802,Type=Trigger.TowerElysionClear,ConditionId=20040,Value=1},new TriggerModelNew{UserId=self,Type=Trigger.TowerMissilisClear,ConditionId=20050,Value=1});db.SaveChanges();}
    Check((await Board(self,5)).User.Score==30,"durable legacy clear must display its floor");
    Check((await Board(self,7)).User.Score==0&&(await Board(self,6)).User.Score==0,"foreign user or mismatched clear leaked into score");
});
await Test("legacy tower achievements display floors without rewriting their rank or time",async()=>
{
    CompletionTargets();Player(self).Ranking=new(){Achievements={[30101]=new(){Rank=1,Score=10030,AchievedAt=777}}};
    var history=await Send<ResGetRankAchievementLeaderBoard>(self,"rankachievement",new ReqGetRankAchievementLeaderBoard{Type=5});
    var row=history.RankAchievement.Single();Check(row.Score==30&&row.Rank==1&&row.AchievedAt==777,"legacy stage ID leaked into achievement response");
    Check(Player(self).Ranking!.Achievements[30101].Score==10030,"display conversion rewrote historical storage");
});
await Test("museum overview has separate total and per hall winners",async()=>
{
    EarnMuseum(self,910001,101,10);EarnMuseum(9802,910002,201,20);
    var overview=await Send<ResGetAllTopRank>(self,"alltoprank",new ReqGetAllTopRank());
    Check(overview.TopMuseumTotalRankingList.Single() is{Type:10,Score:20,User.Usn:9802},"museum total winner missing");
    Check(overview.TopMuseumGroupRankingList.Count==3,"three museum hall cards missing");
    Check(overview.TopMuseumGroupRankingList.Single(r=>r.ConditionId==1) is{Type:11,Score:10,User.Usn:(long)self},"hall one winner wrong");
    Check(overview.TopMuseumGroupRankingList.Single(r=>r.ConditionId==2) is{Score:20,User.Usn:9802},"hall two winner wrong");
    Check(overview.TopMuseumGroupRankingList.Single(r=>r.ConditionId==3) is{Score:0,User:null},"unplayed hall invented a winner");
});
await Test("museum details rank only earned points and preserve the personal zero row",async()=>
{
    EarnMuseum(self,910001,101,10);EarnMuseum(self,910002,201,20);EarnMuseum(9802,910003,201,30);
    var total=await MuseumTotal(self);Check(total.User is{Rank:1,Score:30}&&total.RankingList.Select(r=>r.User.Usn).SequenceEqual(new long[]{(long)self,9802}),"total points or stable ties wrong");
    var hall=await MuseumHall(self,2);Check(hall.User is{Rank:2,Score:20}&&hall.RankingList[0].User.Usn==9802,"hall ranking mixed total points");
    Check((await MuseumHall(self,3)).User is{Rank:0,Score:0,User.Usn:(long)self},"zero personal museum profile missing");
});
await Test("museum receipts reject duplicates unknown stages and the wrong mode",async()=>
{
    EarnMuseum(self,910001,101,10);var stage=Player(self).SoloRaidMuseumData[101];stage.ReceivedChallengeMissions.AddRange([910001,999999]);stage.ReceivedNoLimitMissions.Add(910001);
    EarnMuseum(self,910002,201,20);stage.ReceivedChallengeMissions.Add(910002);Player(self).SoloRaidMuseumData.Remove(201);
    Check((await MuseumTotal(self)).User.Score==10,"invalid or duplicate receipts inflated ranking points");
    var mission=await Send<ResGetSoloRaidMuseumMission>(self,"/soloraidmuseum/get/mission",new ReqGetSoloRaidMuseumMission{StageId=101});
    Check(mission.MuseumTotalRankingPoint==10&&mission.MuseumGroupRankingPoint==10,"museum mission and leaderboard point sources diverged");
});
await Test("tower milestones require the specified floor and museum milestones their hall",async()=>
{
    var table=RankingTable();table.Clear();
    table[30101]=new(){Id=30101,RankingType=RankingType.TowerTribe,ConditionId=10030,ConditionValue=1,ServerRewardId=15000001};
    table[400001]=new(){Id=400001,RankingType=RankingType.MuseumTotal,ConditionValue=30,ServerRewardId=15000001};
    table[401001]=new(){Id=401001,RankingType=RankingType.MuseumGroup,ConditionId=1,ConditionValue=15,ServerRewardId=15000001};
    Player(self).TowerProgress[CorporationTowerType.ALL]=29;EarnMuseum(self,910001,201,30);
    Check((await Achievements(self)).TriggerList.Single(t=>t.Trigger==31).UserValue==1,"tower floor or foreign hall awarded early");
    Player(self).TowerProgress[CorporationTowerType.ALL]=30;EarnMuseum(self,910002,101,15);
    Check((await Achievements(self)).TriggerList.All(t=>t.UserValue==3),"added categories did not credit cumulative achievements");
    var tower=await Send<ResGetRankAchievementLeaderBoard>(self,"rankachievement",new ReqGetRankAchievementLeaderBoard{Type=5});
    var total=await Send<ResGetMuseumTotalRankAchievementLeaderBoard>(self,"get/museumtotal/rankachievement",new ReqGetMuseumTotalRankAchievementLeaderBoard{Type=10});
    var hall=await Send<ResGetMuseumGroupRankAchievementLeaderBoard>(self,"get/museumgroup/rankachievement",new ReqGetMuseumGroupRankAchievementLeaderBoard{Type=11,ConditionId=1});
    Check(tower.RankAchievement.Single().RankId==30101&&total.RankAchievement.Single().RankId==400001&&hall.RankAchievement.Single().RankId==401001,"category milestone details missing");
    string before=JsonConvert.SerializeObject(State());await Achievements(self);await MuseumTotal(self);Check(JsonConvert.SerializeObject(State())==before,"refresh duplicated new ranking milestones");
});
await Test("museum hall reward claims are isolated and all claim remains idempotent",async()=>
{
    var table=RankingTable();table.Clear();
    table[401001]=new(){Id=401001,RankingType=RankingType.MuseumGroup,ConditionId=1,ConditionValue=15,ServerRewardId=15000001};
    table[402001]=new(){Id=402001,RankingType=RankingType.MuseumGroup,ConditionId=2,ConditionValue=20,ServerRewardId=15000001};
    table[400001]=new(){Id=400001,RankingType=RankingType.MuseumTotal,ConditionValue=30,ServerRewardId=15000001};
    table[30101]=new(){Id=30101,RankingType=RankingType.TowerTribe,ConditionId=10030,ConditionValue=1,ServerRewardId=15000001};
    EarnMuseum(9802,910001,101,15);EarnMuseum(9802,910002,201,20);Player(9802).TowerProgress[CorporationTowerType.ALL]=30;
    Check((await MuseumHall(self,1)).RewardEnabled,"earned hall server gift absent for unranked user");
    await HallClaim(1);await HallClaim(1);Check(ItemCount()==1&&!(await MuseumHall(self,1)).RewardEnabled&&(await MuseumHall(self,2)).RewardEnabled,"hall claim crossed category or paid twice");
    await HallClaim(2);Check(ItemCount()==2,"hall two reward missing");
    await Send<ResObtainMuseumTotalRankAchievementServerReward>(self,"obtain/museumtotal/rankachievementserverreward",new ReqObtainMuseumTotalRankAchievementServerReward{Type=10});Check(ItemCount()==3,"museum total reward missing");
    await Send<ResObtainRankAchievementServerReward>(self,"rankachievementserverreward",new ReqObtainRankAchievementServerReward{Type=5});await AllClaim(self);Check(ItemCount()==4,"tower gift missing or all claim duplicated a receipt");
});
await Test("successful tower clears observe the first winner before another player advances",async()=>
{
    RankingTable().Clear();RankingTable()[30101]=new(){Id=30101,RankingType=RankingType.TowerTribe,ConditionId=10001,ConditionValue=1,ServerRewardId=15000001};
    await Send<ResClearTower>(self,"/tower/cleartower",new ReqClearTower{TowerId=10001,TeamNumber=1,BattleResult=1});
    await Send<ResClearTower>(9802,"/tower/cleartower",new ReqClearTower{TowerId=10001,TeamNumber=1,BattleResult=1});
    await Send<ResClearTower>(9802,"/tower/cleartower",new ReqClearTower{TowerId=10002,TeamNumber=1,BattleResult=1});
    var history=await Send<ResGetRankAchievementLeaderBoard>(self,"rankachievement",new ReqGetRankAchievementLeaderBoard{Type=5});
    Check(history.RankAchievement.Single(r=>r.Rank==1).User.Usn==(long)self,"later tower progress stole the first observed winner");
});
await Test("successful museum mission claims observe their first winner",async()=>
{
    RankingTable().Clear();RankingTable()[400001]=new(){Id=400001,RankingType=RankingType.MuseumTotal,ConditionValue=10,ServerRewardId=15000001};
    EarnMuseum(self,910001,101,10);Player(self).SoloRaidMuseumData[101].ReceivedChallengeMissions.Clear();
    EarnMuseum(9802,910002,201,20);Player(9802).SoloRaidMuseumData[201].ReceivedChallengeMissions.Clear();
    await Send<ResAcquireSoloRaidMuseumChallengeMission>(self,"/soloraidmuseum/challenge/acquire/mission",new ReqAcquireSoloRaidMuseumChallengeMission{MissionIdList={910001}});
    await Send<ResAcquireSoloRaidMuseumChallengeMission>(9802,"/soloraidmuseum/challenge/acquire/mission",new ReqAcquireSoloRaidMuseumChallengeMission{MissionIdList={910002}});
    var history=await Send<ResGetMuseumTotalRankAchievementLeaderBoard>(self,"get/museumtotal/rankachievement",new ReqGetMuseumTotalRankAchievementLeaderBoard{Type=10});
    Check(history.RankAchievement.Single(r=>r.Rank==1).User.Usn==(long)self,"later museum points stole the first observed winner");
});
await Test("museum invalid groups types and anonymous reads cannot settle or claim",async()=>
{
    EarnMuseum(9802,910001,101,10);string before=JsonConvert.SerializeObject(State());
    await Reject(()=>MuseumHall(self,999));await Reject(()=>Send<ResGetMuseumTotalRankingList>(self,"get/museumtotal/rankinglist",new ReqGetMuseumTotalRankingList{Type=5}));
    await Reject(()=>Board(self,10));await Reject(()=>MuseumTotal(0));
    Check(JsonConvert.SerializeObject(State())==before,"rejected museum request changed ranking state");
});
await Test("tower and museum scores milestones and reward receipts survive a fresh process",async()=>
{
    CompletionTargets();Player(self).TowerProgress[CorporationTowerType.ALL]=30;EarnMuseum(self,910001,101,10);
    await AllClaim(self);Check(ItemCount()==2,"initial category rewards missing");
    using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--verify-completion-saved"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
    var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();Check(child.ExitCode==0,(await output)+(await error));
});
await Test("ranking profiles expose each representative team without changing growth scores",()=>WithCombatTables(async()=>
{
    Targets(true);
    CombatCharacter(self,1,99000001,2);Player(self).RepresentationTeamDataNew=[1,0,0,0,0];
    CombatCharacter(9802,1,99000001,1);CombatCharacter(9802,2,99000002,2);
    Player(9802).ManagedNpc=new();Player(9802).RepresentationTeamDataNew=[1,0,999,0,0];
    CombatCharacter(9803,1,99000002,2);Player(9803).ManagedNpc=new();Player(9803).RepresentationTeamDataNew=[1,0,0,0,0];
    CombatCharacter(9804,1,99000001,1);Player(9804).ManagedNpc=new();
    var board=await Board(self,2);
    Check(board.User.User.TeamCombat==236,"personal ranking profile lost team combat");
    Check(board.RankingList.Single(r=>r.User.Usn==9802).User.TeamCombat==118,"NPC combat must use valid selected characters, not its whole roster");
    Check(board.RankingList.Single(r=>r.User.Usn==9803).User.TeamCombat==236,"distinct NPC teams were assigned the same or zero combat");
    Check(board.RankingList.Single(r=>r.User.Usn==9804).User.TeamCombat==0,"empty representative team invented combat");
    int score=board.RankingList.Single(r=>r.User.Usn==9802).Score;
    Player(9802).RepresentationTeamDataNew=[2,0,0,0,0];
    var changed=(await Board(self,2)).RankingList.Single(r=>r.User.Usn==9802);
    Check(changed.User.TeamCombat==236&&changed.Score==score,"team change must refresh combat independently of corporation growth points");
}));
await Test("overview achievements and museum profiles include current Synchro team combat",()=>WithCombatTables(async()=>
{
    Targets(true);CombatCharacter(9802,1,99000001,1);var npc=Player(9802);npc.ManagedNpc=new();
    npc.RepresentationTeamDataNew=[1,0,0,0,0];npc.LastHardStageCleared=7001001;npc.TowerProgress[CorporationTowerType.ALL]=30;
    EarnMuseum(9802,910001,101,10);
    Check((await Board(self,2)).RankingList.Single().User.TeamCombat==118,"initial NPC combat absent");
    npc.SynchroDeviceUpgraded=true;npc.SynchroDeviceLevel=2;npc.SynchroSlots.Add(new(){Slot=1,CharacterSerialNumber=1});
    var overview=await Send<ResGetAllTopRank>(self,"alltoprank",new ReqGetAllTopRank());
    foreach(int type in new[]{0,2,5})Check(overview.TopRankingList.Single(r=>r.Type==type).User.TeamCombat==236,"overview used missing or stale Synchro combat");
    Check(overview.TopMuseumTotalRankingList.Single().User.TeamCombat==236&&overview.TopMuseumGroupRankingList.Single(r=>r.ConditionId==1).User.TeamCombat==236,"museum overview lost team combat");
    var history=await Send<ResGetRankAchievementLeaderBoard>(self,"rankachievement",new ReqGetRankAchievementLeaderBoard{Type=2});
    Check(history.RankAchievement.Count>0&&history.RankAchievement.All(r=>r.User.TeamCombat==236),"achievement profiles lost current team combat");
    Check((await MuseumTotal(self)).RankingList.Single().User.TeamCombat==236&&(await MuseumHall(self,1)).RankingList.Single().User.TeamCombat==236,"museum detail profiles lost team combat");
    Check((await Board(self,0)).RankingList.Single().User.TeamCombat==236&&(await Board(self,5)).RankingList.Single().User.TeamCombat==236,"campaign or tower detail lost team combat");
}));
Console.WriteLine($"Ranking regression: {passed} passed, {failed} failed");return failed==0?0:1;
CoreInfo Fixture()=>new(){DbVersion=5,LauncherTokenKey=key,EncryptionTokenKey=key,BondRewardLevelsMigrated=true,Users=Enumerable.Range(9801,13).Select(i=>new User{ID=(ulong)i}).ToList()};
User Player(ulong id)=>State().Users.Single(u=>u.ID==id);
void Add(ulong id,int corp,int grade){var c=data.CharacterTable.Values.Where(c=>(int)c.Corporation==corp&&c.OriginalRare==OriginalRareType.SSR&&c.GradeCoreId==grade).OrderBy(c=>c.Id).First();Player(id).Characters.Add(new(){Csn=Player(id).Characters.Count+1,Tid=c.Id,Grade=grade-1});}
int ItemCount()=>Player(self).Items.Where(i=>i.ItemType==9201001).Sum(i=>i.Count);
Dictionary<int,RankingRecord> RankingTable()=>(Dictionary<int,RankingRecord>)(typeof(GameData).GetField("Rankings")?.GetValue(data)??throw new InvalidOperationException("RankingTable is not loaded"));
void Targets(bool custom){var f=typeof(GameData).GetField("Rankings");if(f==null)return;var table=(Dictionary<int,RankingRecord>)f.GetValue(data)!;table.Clear();foreach(var t in rawTargets)table[t.Id]=t;if(custom){table.Clear();for(int n=1;n<=10;n++)table[10200+n]=new(){Id=10200+n,RankingType=RankingType.GroupPointElysion,ConditionValue=n*300,ServerRewardId=15000001};}}
async Task Test(string name,Func<Task> test){try{property.SetValue(null,Fixture());using(var db=GameContext.CreateNew()){db.Triggers.ExecuteDelete();}Targets(false);data.MuseumMissionTable.Clear();foreach(var pair in rawMuseumMissions)data.MuseumMissionTable.Add(pair.Key,pair.Value);await test();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e);}}
void EarnMuseum(ulong id,int missionId,int stageId,int points){data.MuseumMissionTable[missionId]=new(){Id=missionId,StageId=stageId,RankingPointAmount=points,ModeType=MuseumStageModeType.Challenge,ConditionValue=0};if(!Player(id).SoloRaidMuseumData.TryGetValue(stageId,out var stage))Player(id).SoloRaidMuseumData[stageId]=stage=new(){StageId=stageId};stage.ReceivedChallengeMissions.Add(missionId);}
Task<ResGetMuseumTotalRankingList> MuseumTotal(ulong id)=>Send<ResGetMuseumTotalRankingList>(id,"get/museumtotal/rankinglist",new ReqGetMuseumTotalRankingList{Type=10});
Task<ResGetMuseumGroupRankingList> MuseumHall(ulong id,int group)=>Send<ResGetMuseumGroupRankingList>(id,"get/museumgroup/rankinglist",new ReqGetMuseumGroupRankingList{Type=11,ConditionId=group});
Task<ResObtainMuseumGroupRankAchievementServerReward> HallClaim(int group)=>Send<ResObtainMuseumGroupRankAchievementServerReward>(self,"obtain/museumgroup/rankachievementserverreward",new ReqObtainMuseumGroupRankAchievementServerReward{Type=11,ConditionId=group});
async Task Reject(Func<Task> action){try{await action();}catch(BadHttpRequestException){return;}catch(UnauthorizedAccessException){return;}throw new Exception("invalid museum request accepted");}
void CompletionTargets(){RankingTable().Clear();RankingTable()[30101]=new(){Id=30101,RankingType=RankingType.TowerTribe,ConditionId=10030,ConditionValue=1,ServerRewardId=15000001};RankingTable()[400001]=new(){Id=400001,RankingType=RankingType.MuseumTotal,ConditionValue=10,ServerRewardId=15000001};data.MuseumMissionTable[910001]=new(){Id=910001,StageId=101,RankingPointAmount=10,ModeType=MuseumStageModeType.Challenge};}
Task<ResGetRankingList> Board(ulong id,int type)=>Send<ResGetRankingList>(id,"getlist",new ReqGetRankingList{Type=type});
Task<ResGetRankAchievementTop> Achievements(ulong id)=>Send<ResGetRankAchievementTop>(id,"rankachievementtop",new ReqGetRankAchievementTop());
Task<ResObtainAllRankAchievementServerReward> AllClaim(ulong id)=>Send<ResObtainAllRankAchievementServerReward>(id,"allrankachievementserverreward",new ReqObtainAllRankAchievementServerReward());
async Task<T> Send<T>(ulong id,string route,IMessage req)where T:IMessage<T>,new(){using var scope=services.CreateScope();var ctx=new DefaultHttpContext{RequestServices=scope.ServiceProvider};ctx.Request.Path=route.StartsWith('/')?"/v1"+route:"/v1/ranking/"+route;ctx.Request.Body=new MemoryStream(req.ToByteArray());ctx.Response.Body=new MemoryStream();if(id!=0)ctx.Request.Headers.Authorization="Bearer "+new PasetoBuilder().Use(ProtocolVersion.V4,Purpose.Local).WithKey(key,Encryption.SymmetricKey).AddClaim("userId",id).Expiration(DateTime.UtcNow.AddMinutes(10)).Encode();await LobbyHandler.DispatchSingle(ctx);T reply=new();reply.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray());return reply;}
static void Check(bool b,string m){if(!b)throw new InvalidOperationException(m);}
void CombatCharacter(ulong id,int csn,int tid,int level)=>Player(id).Characters.Add(new(){Csn=csn,Tid=tid,Level=level});
async Task WithCombatTables(Func<Task> test)
{
    int[] ids=[99000001,99000002];
    Check(ids.All(id=>!data.CharacterTable.ContainsKey(id)&&!data.characterStatTable.ContainsKey(id)),"combat fixture IDs overlap shipped tables");
    foreach(int id in ids)data.CharacterTable.Add(id,new(){Id=id,NameCode=id,OriginalRare=OriginalRareType.SSR,GradeCoreId=1,Corporation=CorporationType.ELYSION,StatEnhanceId=99000001});
    // With no critical bonus/core and level-one skills: (100*18 + 10000*.7)*1.34/100 = 117.92 -> 118 CP.
    // Doubling the level stats gives 235.84 -> 236 CP, independently of the implementation.
    data.characterStatTable.Add(99000001,new(){Id=99000001,Group=99000001,Level=1,LevelHp=10000,LevelAttack=100});
    data.characterStatTable.Add(99000002,new(){Id=99000002,Group=99000001,Level=2,LevelHp=20000,LevelAttack=200});
    try{await test();}finally{foreach(int id in ids){data.CharacterTable.Remove(id);data.characterStatTable.Remove(id);}}
}
