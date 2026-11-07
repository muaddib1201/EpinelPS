using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using Newtonsoft.Json;

internal static class GrowthChecks
{
    private static readonly Assembly Assembly = typeof(GameContext).Assembly;
    private static readonly Type Service = Assembly.GetType("EpinelPS.Services.ManagedNpcService")!;
    private static readonly Type JsonDb = Assembly.GetType("EpinelPS.Database.JsonDb")!;
    private static readonly PropertyInfo Instance = JsonDb.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
    private static Type Growth => Assembly.GetType("EpinelPS.Services.ManagedNpcGrowth") ?? throw new Exception("FAIL: NPC synchro/duplicate growth is unavailable");
    private static readonly DateTime At = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
    private static int Level(User user, CharacterModel c) => (int)typeof(User).GetMethod("GetCharacterLevel", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(int),typeof(int)])!.Invoke(user,[c.Csn,c.Level])!;
    private static User Npc(GameData data, int level=200, int stars=3)
    {
        int[] names=[5011,5036,5024,5001,5066,5030];
        var u=new User { ID=1500000000, Description="一步一步前進。", ManagedNpc=new(){Name="星光守望",Seed=1234,Enabled=false,CreatedUtc=At,NextSkillUtc=At.AddYears(1),NextRecruitUtc=At.AddYears(1)}, RepresentationTeamDataNew=[10001,10002,10003,10004,10005] };
        foreach(int name in names){var row=data.CharacterTable.Values.Where(r=>r.NameCode==name&&r.GradeCoreId==stars+1).OrderBy(r=>r.Id).First();u.Characters.Add(new(){Csn=10001+u.Characters.Count,Tid=row.Id,Grade=stars,Level=level,Skill1Lvl=5,Skill2Lvl=5,UltimateLevel=5});}
        return u;
    }
    public static int Run(GameData data)
    {
        _=Growth;
        var u=Npc(data,228);
        var teams=(long[])u.RepresentationTeamDataNew.Clone();
        Call(Growth,"Apply",u);
        Check(u.SynchroDeviceUpgraded&&u.SynchroDeviceLevel==228&&u.Characters.All(c=>c.Level<=200)&&u.Characters.All(c=>Level(u,c)==228),"legacy228 must become capped bases and enhanced synchro228 without effective-level loss");
        Check(teams.SequenceEqual(u.RepresentationTeamDataNew)&&u.Characters.Count==6&&u.Characters.All(c=>c.Grade==3),"migration preserves teams, ownership and existing stars");
        string before=JsonConvert.SerializeObject(u);Call(Growth,"Apply",u);Check(before==JsonConvert.SerializeObject(u),"growth migration must be idempotent");
        Console.WriteLine("PASS:228-level migration preserves effective levels and uses capped bases plus valid enhanced synchro");
        var mixed=Npc(data,220);mixed.Characters[0].Level=228;Call(Growth,"Apply",mixed);Check(mixed.SynchroDeviceLevel==228&&Level(mixed,mixed.Characters[0])==228,"mixed legacy levels must preserve the highest effective level when prerequisites allow synchro");
        var incompatible=Npc(data,199);incompatible.Characters[0].Level=228;string incompatibleBefore=JsonConvert.SerializeObject(incompatible);bool rejected=false;try{Call(Growth,"Apply",incompatible);}catch(InvalidDataException){rejected=true;}Check(rejected&&JsonConvert.SerializeObject(incompatible)==incompatibleBefore,"legacy228 without five200 SSRs must reject before capping levels or granting enhanced synchro");

        var gated=Npc(data,199);Call(Growth,"Apply",gated);Check(!gated.SynchroDeviceUpgraded,"five three-stars below200 cannot unlock enhanced synchro");Call(Growth,"Advance",gated);Check(gated.SynchroDeviceUpgraded&&gated.SynchroDeviceLevel==200,"five actual level200 three-stars unlock enhanced synchro");Call(Growth,"Advance",gated);Check(gated.SynchroDeviceLevel==201&&gated.Characters.All(c=>c.Level<=200),"post200 growth changes the device rather than base levels");
        var wall=Npc(data,160,0);Call(Growth,"Apply",wall);Call(Growth,"Advance",wall);Check(!wall.SynchroDeviceUpgraded&&wall.Characters.All(c=>c.Level==160&&c.Grade==0),"zero-star SSRs stay at160 until duplicate recruitment breaks the wall");
        var low=new CharacterModel{Csn=10007,Tid=117201,Level=1,Grade=0};u.Characters.Add(low);Call(Growth,"Apply",u);Check(low.Level==1&&low.Grade==0&&Level(u,low)==228,"synchro can lift a zero-star level1 recruit above200 without granting stars");
        Console.WriteLine("PASS: five200 prerequisites,160 wall, device growth and zero-star synchro are distinct");
        int cap=(int)Call(Growth,"MaximumSynchroLevel",gated)!;gated.SynchroDeviceLevel=cap;Call(Growth,"Advance",gated);Check(gated.SynchroDeviceLevel==cap,"device growth must stop at the owned-collection synchro cap");
        var repeatedNames=Npc(data,200);repeatedNames.Characters[4].Tid=repeatedNames.Characters[0].Tid;repeatedNames.Characters[5].Tid=repeatedNames.Characters[1].Tid;Call(Growth,"Apply",repeatedNames);Check(!repeatedNames.SynchroDeviceUpgraded,"duplicate character instances cannot satisfy five distinct200 SSR prerequisites");

        var duplicate=Npc(data,200);Call(Growth,"Apply",duplicate);int count=duplicate.Characters.Count;var original=duplicate.Characters.Select(c=>(c.Csn,c.Level,c.Skill1Lvl,c.Skill2Lvl,c.UltimateLevel)).ToArray();
        duplicate.ManagedNpc!.GetType().GetProperty("RecruitSequence")!.SetValue(duplicate.ManagedNpc,1L);
        Call(Growth,"Recruit",duplicate,new Random(7));Check(duplicate.Characters.Count==count&&duplicate.Characters.Count(c=>c.Grade==4)==1,"a repeated three-star SSR becomes core+1 without another owned character");
        Check(original.SequenceEqual(duplicate.Characters.Select(c=>(c.Csn,c.Level,c.Skill1Lvl,c.Skill2Lvl,c.UltimateLevel))),"duplicate recruitment preserves CSNs, levels and skills");
        foreach(var c in duplicate.Characters){var row=data.CharacterTable[c.Tid];c.Tid=data.CharacterTable.Values.Single(r=>r.NameCode==row.NameCode&&r.GradeCoreId==11).Id;c.Grade=10;}
        duplicate.Characters[0].Tid=data.CharacterTable.Values.Single(r=>r.NameCode==5011&&r.GradeCoreId==10).Id;duplicate.Characters[0].Grade=9;
        duplicate.ManagedNpc.GetType().GetProperty("RecruitSequence")!.SetValue(duplicate.ManagedNpc,1L);Call(Growth,"Recruit",duplicate,new Random(7));Check(duplicate.Characters[0].Grade==10&&data.CharacterTable[duplicate.Characters[0].Tid].GradeCoreId==11,"core+6 advances to real core+7 template");
        duplicate.ManagedNpc.GetType().GetProperty("RecruitSequence")!.SetValue(duplicate.ManagedNpc,3L);Call(Growth,"Recruit",duplicate,new Random(7));Check(duplicate.Characters.All(c=>c.Grade<=10)&&duplicate.Characters.Count==count+1,"all max cores fall back to a new zero-star recruit without core+8");
        Check(duplicate.Characters.Last().Level==1&&duplicate.Characters.Last().Grade==0,"new recruitment starts at base level1 and zero stars");
        Console.WriteLine("PASS: repeated recruitment follows real core+1 through+7 templates and preserves owned identities");

        string path=Path.Combine(AppContext.BaseDirectory,"db.json");var human=Npc(data,228);human.ID=1;human.ManagedNpc=null;
        File.WriteAllText(path,JsonConvert.SerializeObject(new CoreInfo{Users=[human,Npc(data,228)],NpcManagement=null,BondRewardLevelsMigrated=true}));
        var core=(CoreInfo)Instance.GetValue(null)!;before=JsonConvert.SerializeObject(core);Directory.CreateDirectory(path+".tmp");bool failed=false;
        try{Call(Service,"UpgradeGrowth");}catch(IOException){failed=true;}catch(UnauthorizedAccessException){failed=true;}finally{Directory.Delete(path+".tmp");}
        Check(failed&&JsonConvert.SerializeObject(Instance.GetValue(null))==before,"failed migration save must restore the entire prior snapshot");
        string humanBefore=JsonConvert.SerializeObject(core.Users.Single(x=>x.ID==1));Call(Service,"UpgradeGrowth");core=(CoreInfo)Instance.GetValue(null)!;
        Check(core.NpcManagement==null&&JsonConvert.SerializeObject(core.Users.Single(x=>x.ID==1))==humanBefore,"growth migration must preserve human saves and absent/paused settings");
        before=JsonConvert.SerializeObject(core);Directory.CreateDirectory(path+".tmp");try{Call(Service,"UpgradeGrowth");}finally{Directory.Delete(path+".tmp");}Check(JsonConvert.SerializeObject(Instance.GetValue(null))==before,"already migrated state must not be rewritten");
        Console.WriteLine("PASS: growth snapshot rollback, human preservation, null settings and migration idempotence");return 0;
    }
    private static object? Call(Type t,string name,params object[] args){try{return t.GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static,null,args.Select(a=>a.GetType()).ToArray(),null)!.Invoke(null,args);}catch(TargetInvocationException e){throw e.InnerException!;}}
    private static void Check(bool condition,string message){if(!condition)throw new Exception("FAIL: "+message);}
}
