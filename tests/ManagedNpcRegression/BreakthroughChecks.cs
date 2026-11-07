using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using Newtonsoft.Json;

internal static class BreakthroughChecks
{
    private static readonly Assembly Assembly = typeof(GameContext).Assembly;
    private static readonly Type Factory = Assembly.GetType("EpinelPS.Services.ManagedNpcFactory")!;
    private static readonly Type Service = Assembly.GetType("EpinelPS.Services.ManagedNpcService")!;
    private static readonly Type JsonDb = Assembly.GetType("EpinelPS.Database.JsonDb")!;
    private static readonly PropertyInfo Instance = JsonDb.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;

    public static int Run(GameData data)
    {
        User Npc(int level, int grade = 0, int tid = 117201) => new()
        {
            ID = 1500000000, Description = "慢慢前進，也記得照顧同行的朋友。",
            ManagedNpc = new() { Name = "星光守望", Seed = 1234, Enabled = false, NextActionUtc = new(2030, 1, 1) },
            RepresentationTeamDataNew = [10001],
            Characters = [new() { Csn = 10001, Tid = tid, Grade = grade, Level = level,
                Skill1Lvl = 6, Skill2Lvl = 5, UltimateLevel = 7, CostumeId = 0 }]
        };
        var created = Npc(1);
        created.Characters.Clear();
        Call(Factory, "AddCharacter", created, data.CharacterTable[117201], 171, 2);
        Check(created.Characters.Single().Grade == 3 && created.Characters.Single().Tid == 117204,
            "new level-171 SSR must use the actual three-star template 117204, not base template 117201");
        var boundary = Npc(1);
        boundary.Characters.Clear();
        Call(Factory, "AddCharacter", boundary, data.CharacterTable[117201], 160, 2);
        Check(boundary.Characters.Single().Grade == 0 && boundary.Characters.Single().Tid == 117201,
            "level-160 base SSR remains at its existing zero-star breakthrough");
        Console.WriteLine("PASS: new NPC SSRs and recruited characters receive matching grades and templates at the level boundary");

        if (Service.GetMethod("UpgradeBreakthroughs") == null)
            throw new Exception("FAIL: existing NPC breakthrough repair is unavailable");
        var human = new User { ID = 1, Characters = [new() { Csn = 1, Tid = 117201, Grade = 3, Level = 171 }] };
        var npc = Npc(171, 3);
        npc.Characters.Add(new() { Csn = 10002, Tid = 117201, Grade = 0, Level = 161 });
        npc.Characters.Add(new() { Csn = 10003, Tid = 117208, Grade = 0, Level = 171 });
        npc.Characters.Add(new() { Csn = 10004, Tid = 117201, Grade = 1, Level = 80 });
        npc.Characters.Add(new() { Csn = 10005, Tid = 117201, Grade = 2, Level = 120 });
        npc.Characters.Add(new() { Csn = 10006, Tid = 201001, Grade = 2, Level = 100 });
        npc.Characters.Add(new() { Csn = 10007, Tid = 117201, Grade = 0, Level = 160 });
        string path = Path.Combine(AppContext.BaseDirectory, "db.json");
        File.WriteAllText(path, JsonConvert.SerializeObject(new CoreInfo
        {
            Users = [human, npc], NpcManagement = new() { Enabled = false }, BondRewardLevelsMigrated = true
        }));
        var core = (CoreInfo)Instance.GetValue(null)!;
        var expected = Clone(core);
        var expectedChars = expected.Users.Single(u => u.ManagedNpc != null).Characters;
        foreach (var (csn, tid, grade) in new[] { (10001, 117204, 3), (10002, 117204, 3), (10003, 117208, 7),
            (10004, 117202, 1), (10005, 117203, 2), (10006, 201003, 2) })
        {
            var c = expectedChars.Single(c => c.Csn == csn); c.Tid = tid; c.Grade = grade;
        }
        Call(Service, "UpgradeBreakthroughs");
        core = (CoreInfo)Instance.GetValue(null)!;
        Check(JsonConvert.SerializeObject(core) == JsonConvert.SerializeObject(expected),
            "repair only NPC Tid/Grade, preserve core upgrades, humans, profiles, teams, levels, skills and paused schedules");
        string before = JsonConvert.SerializeObject(core);
        Directory.CreateDirectory(path + ".tmp");
        try { Call(Service, "UpgradeBreakthroughs"); }
        finally { Directory.Delete(path + ".tmp"); }
        Check(JsonConvert.SerializeObject(Instance.GetValue(null)) == before, "idempotent repair must not save or change an already-repaired snapshot");
        Console.WriteLine("PASS: existing NPC repair preserves higher cores and unrelated data; repeat repair performs no write");

        var old = ((CoreInfo)Instance.GetValue(null)!).Users.Single(u => u.ManagedNpc != null).Characters[0];
        old.Tid = 117201;
        before = JsonConvert.SerializeObject(Instance.GetValue(null));
        Directory.CreateDirectory(path + ".tmp");
        bool failed = false;
        try { Call(Service, "UpgradeBreakthroughs"); }
        catch (IOException) { failed = true; }
        catch (UnauthorizedAccessException) { failed = true; }
        finally { Directory.Delete(path + ".tmp"); }
        Check(failed && JsonConvert.SerializeObject(Instance.GetValue(null)) == before,
            "failed repair save must roll back the complete NPC snapshot");
        Call(Service, "UpgradeBreakthroughs");
        Call(JsonDb, "Reload");
        before = JsonConvert.SerializeObject(Instance.GetValue(null));
        Call(Service, "UpgradeBreakthroughs");
        Check(JsonConvert.SerializeObject(Instance.GetValue(null)) == before, "reload must preserve repaired variants and idempotence");
        Console.WriteLine("PASS: failed-save rollback and reload preserve coherent breakthrough data");

        var pausedCore = (CoreInfo)Instance.GetValue(null)!;
        pausedCore.NpcManagement = null;
        pausedCore.Users.Single(u => u.ManagedNpc != null).Characters[0].Tid = 117201;
        var expectedPaused = Clone(pausedCore);
        expectedPaused.Users.Single(u => u.ManagedNpc != null).Characters[0].Tid = 117204;
        Call(Service, "UpgradeBreakthroughs");
        Check(JsonConvert.SerializeObject(Instance.GetValue(null)) == JsonConvert.SerializeObject(expectedPaused),
            "repair must preserve null management settings instead of enabling global NPC scheduling");
        Console.WriteLine("PASS: absent global scheduling settings remain absent and paused during repair");

        var at = new DateTime(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc);
        var growing = Npc(160);
        growing.ManagedNpc!.CreatedUtc = at;
        growing.ManagedNpc.NextRecruitUtc = growing.ManagedNpc.NextSkillUtc = at.AddYears(1);
        Call(Service, "Act", growing, at);
        Check(growing.Characters.Single().Level == 160 && growing.Characters.Single().Grade == 0 && growing.Characters.Single().Tid == 117201,
            "level-160 growth must wait for real duplicate breakthroughs rather than granting three stars");
        var recruited = (User)Call(Factory, "Create", 1500000001UL, 0, at)!;
        foreach (var c in recruited.Characters) c.Level = 200;
        recruited.ManagedNpc!.NextRecruitUtc = at;
        recruited.ManagedNpc.NextSkillUtc = at.AddYears(1);
        int maxCsn = recruited.Characters.Max(c => c.Csn);
        Call(Service, "Act", recruited, at);
        var added = recruited.Characters.Single(c => c.Csn > maxCsn);
        Check(added.Level == 1 && added.Grade == 0 && data.CharacterTable[added.Tid].GradeCoreId == 1 && recruited.SynchroSlots.Any(s => s.CharacterSerialNumber == added.Csn),
            "scheduled new recruitment starts at zero stars and joins synchro without artificial breakthroughs");
        Console.WriteLine("PASS: real growth respects the160 wall and scheduled recruitment uses zero-star synchro");
        return 0;
    }

    private static CoreInfo Clone(CoreInfo value) => JsonConvert.DeserializeObject<CoreInfo>(JsonConvert.SerializeObject(value))!;
    private static object? Call(Type type, string name, params object[] args)
    {
        try { return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
}
