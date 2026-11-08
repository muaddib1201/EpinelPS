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

const ulong self = 41002, other = 41001; // The first actor deliberately loses an ID-based tie.
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Server and Server selector"))) root = root.Parent;
var data = new GameData(Directory.GetFiles(Path.Combine(root!.FullName, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single());
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
byte[] key = Enumerable.Repeat((byte)83, 32).ToArray();
string save = Path.Combine(AppContext.BaseDirectory, "db.json"), connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "growth.db")}";
bool restart = args.Contains("--verify-saved");
GameContext.InitializeOptions(connection, "sqlite");
if (!restart)
{
    File.WriteAllText(save, JsonConvert.SerializeObject(Fixture()));
    using var db = GameContext.CreateNew(); db.Database.EnsureDeleted(); db.Database.EnsureCreated();
    db.Users.AddRange(new GameUser { ID = self, Nickname = "Growth A" }, new GameUser { ID = other, Nickname = "Growth B" }); db.SaveChanges();
}
var instance = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!.GetProperty("Instance")!;
CoreInfo State() => (CoreInfo)instance.GetValue(null)!;
User Player(ulong id = self) => State().Users.Single(u => u.ID == id);
using var services = new ServiceCollection().AddDbContext<GameContext>(options => options.UseSqlite(connection)).BuildServiceProvider();
Target(1000);
if (restart)
{
    Check((await Board()).User.Score == 1167, "restart lost a scoring component");
    await Send<ResObtainAllRankAchievementServerReward>(self, "/ranking/allrankachievementserverreward", new ReqObtainAllRankAchievementServerReward());
    Check(Player().Items.Where(i => i.ItemType == 9201001).Sum(i => i.Count) == 1, "restart reissued a server reward");
    Console.WriteLine("PASS restored complete corporation growth and reward receipts"); return 0;
}
int passed = 0, failed = 0;
await Test("native level is a one-point-per-level component", async () =>
{
    var c = Add(self); c.Level = 100; Check((await Board()).User.Score == 400, "native level omitted");
    c.Level = 101; Check((await Board()).User.Score == 401, "native level increment wrong");
});
await Test("SR and R retain level points independently of rarity", async () =>
{
    Add(self, rarity: OriginalRareType.SR).Level = 100;
    Check((await Board()).User.Score == 150, "SR native level omitted");
    Player().Characters.Clear(); Add(self, rarity: OriginalRareType.R).Level = 100;
    Check((await Board()).User.Score == 100, "R excluded from growth");
});
await Test("each of three skills uses the cumulative shipped point curve", async () =>
{
    var c = Add(self);
    int[] expected = [301, 316, 331, 346, 391, 436, 481, 571, 661, 751];
    for (int level = 1; level <= 10; level++)
    {
        c.Skill1Lvl = c.Skill2Lvl = c.UltimateLevel = level;
        Check((await Board()).User.Score == expected[level - 1], $"skill level {level} scored incorrectly");
    }
});
await Test("registered Synchro adds its level to native level", async () =>
{
    var c = Add(self); Player().SynchroDeviceUpgraded = true; Player().SynchroDeviceLevel = 350;
    Player().SynchroSlots.Add(new() { Slot = 1, CharacterSerialNumber = c.Csn });
    Check((await Board()).User.Score == 651, "level-1 admission must add 350 points");
    c.Level = 100; Check((await Board()).User.Score == 750, "native level was replaced instead of added");
    Player().SynchroSlots.Clear(); Check((await Board()).User.Score == 400, "unregistered character still received Synchro points");
});
await Test("unenhanced Synchro follows the fifth native level", async () =>
{
    var c = Add(self); foreach (int level in new[] { 200, 190, 180, 170, 160 }) Add(self, corporation: CorporationType.ABNORMAL).Level = level;
    Player().SynchroSlots.Add(new() { Slot = 1, CharacterSerialNumber = c.Csn });
    Check((await Board()).User.Score == 461, "fifth native level missing");
    Player().Characters.Last().Level = 165; Check((await Board()).User.Score == 466, "Synchro level change not reflected");
    Player().Characters.Add(new() { Csn = 99, Tid = int.MaxValue, Level = 1000 });
    Check((await Board()).User.Score == 466, "unknown definition inflated the fifth native level");
});
await Test("manufacturer and matching class research count once per owned character", async () =>
{
    Add(self, characterClass: 1); Add(self, characterClass: 2); Add(self, corporation: CorporationType.TETRA, characterClass: 1);
    Research(self, RecycleType.Corporation, RecycleSubType.ELYSION, 10);
    Research(self, RecycleType.Class, RecycleSubType.Attacker, 7); Research(self, RecycleType.Class, RecycleSubType.Defender, 4);
    Research(self, RecycleType.Personal, RecycleSubType.Personal, 999); Research(self, RecycleType.Corporation, RecycleSubType.MISSILIS, 200);
    Player().ResearchProgress[int.MaxValue] = new() { Level = 1000 };
    Check((await Board()).User.Score == 633, "research type or owned-class multiplier wrong");
    Check((await Board(type: 3)).User.Score == 308, "corporation research leaked across manufacturers");
});
await Test("bond and stored research stats cannot alter growth", async () =>
{
    var c = Add(self); int name = data.CharacterTable[c.Tid].NameCode;
    var research = Research(self, RecycleType.Corporation, RecycleSubType.ELYSION, 1);
    int before = (await Board()).User.Score;
    Player().BondInfo.Add(new() { NameCode = name, Lv = 40, Exp = 999999 });
    research.Attack = research.Defense = research.Hp = int.MaxValue;
    Check((await Board()).User.Score == before && before == 302, "bond or saved stat bonuses counted as growth");
});
await Test("duplicate variants use one whole record rather than joining maxima", async () =>
{
    var c = Add(self); c.Level = 100; c.Skill1Lvl = c.Skill2Lvl = c.UltimateLevel = 10;
    var definition = data.CharacterTable[c.Tid];
    var core = data.CharacterTable.Values.First(d => d.NameCode == definition.NameCode && d.GradeCoreId == 11);
    Player().Characters.Add(new() { Csn = 2, Tid = core.Id, Grade = 10 });
    Check((await Board()).User.Score == 3301, "combined growth from separate duplicate records");
});
await Test("invalid empty Synchro and unknown character records cannot inflate score", async () =>
{
    var c = Add(self); c.Csn = 0; c.Level = -1; c.Skill1Lvl = c.Skill2Lvl = c.UltimateLevel = -1;
    Player().SynchroDeviceUpgraded = true; Player().SynchroDeviceLevel = 1000;
    Player().SynchroSlots.Add(new() { Slot = 1, CharacterSerialNumber = 0 });
    Player().Characters.Add(new() { Csn = 9, Tid = int.MaxValue, Level = int.MaxValue });
    Check((await Board()).User.Score == 300, "empty slot, negative component or unknown definition counted");
});
await Test("actual skill upgrades settle the first crossing before an ID tie", async () =>
{
    Target(306); foreach (ulong id in new[] { self, other }) { Add(id); FundSkill(id); }
    foreach (ulong id in new[] { self, other }) await Send<ResCharacterSkillLevelUp>(id, "/character/skill/levelup", new ReqCharacterSkillLevelUp { Csn = 1, Category = (int)CharacterSkillCategory.Skill1 });
    await FirstWinner();
});
await Test("actual Synchro admissions settle the first crossing before an ID tie", async () =>
{
    Target(600);
    foreach (ulong id in new[] { self, other }) { Add(id); Player(id).SynchroDeviceUpgraded = true; Player(id).SynchroDeviceLevel = 350; Player(id).SynchroSlots.Add(new() { Slot = 1 }); }
    foreach (ulong id in new[] { self, other }) await Send<ResSynchroRegister>(id, "/character/SynchroDevice/Regist", new ReqSynchroRegister { Csn = 1, Slot = 1 });
    await FirstWinner();
});
await Test("actual research upgrades settle the first crossing before an ID tie", async () =>
{
    Target(303); var stat = data.RecycleResearchStats.Values.Single(r => r.RecycleType == RecycleType.Corporation && r.RecycleSubType == RecycleSubType.ELYSION);
    var level = data.RecycleResearchLevels.Values.Single(r => r.RecycleType == stat.RecycleType && r.RecycleSubType == stat.RecycleSubType && r.RecycleLevel == 1);
    foreach (ulong id in new[] { self, other }) { Add(id); Research(id, stat.RecycleType, stat.RecycleSubType, 1); Player(id).Items.Add(new() { Isn = 1, ItemType = level.ItemId, Count = level.ItemValue }); }
    foreach (ulong id in new[] { self, other }) await Send<ResRecycleLevelUpResearch>(id, "/outpost/RecycleRoom/LevelUpResearch", new ReqRecycleLevelUpResearch { Tid = stat.Id, LevelUpCount = 1 });
    await FirstWinner();
});
await Test("complete growth and claimed rewards survive a fresh process", async () =>
{
    Target(1000); var c = Add(self); c.Level = 100; c.Skill1Lvl = c.Skill2Lvl = c.UltimateLevel = 10;
    Player().SynchroDeviceUpgraded = true; Player().SynchroDeviceLevel = 300; Player().SynchroSlots.Add(new() { Slot = 1, CharacterSerialNumber = 1 });
    Research(self, RecycleType.Corporation, RecycleSubType.ELYSION, 10);
    Research(self, RecycleType.Class, (RecycleSubType)((int)data.CharacterTable[c.Tid].Class + 1), 7);
    Check((await Board()).User.Score == 1167, "full four-component sum wrong");
    await Send<ResObtainAllRankAchievementServerReward>(self, "/ranking/allrankachievementserverreward", new ReqObtainAllRankAchievementServerReward());
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--verify-saved") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
    var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync(); await child.WaitForExitAsync(); Check(child.ExitCode == 0, (await stdout) + (await stderr));
});
Console.WriteLine($"Corporation growth regression: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;

CoreInfo Fixture() => new() { DbVersion = 5, BondRewardLevelsMigrated = true, LauncherTokenKey = key, EncryptionTokenKey = key, Users = [new() { ID = self }, new() { ID = other }] };
CharacterModel Add(ulong id, OriginalRareType rarity = OriginalRareType.SSR, CorporationType corporation = CorporationType.ELYSION, int? characterClass = null)
{
    int grade = rarity == OriginalRareType.SSR ? 1 : rarity == OriginalRareType.SR ? 101 : 201;
    var ownedNames = Player(id).Characters.Where(c => data.CharacterTable.ContainsKey(c.Tid)).Select(c => data.CharacterTable[c.Tid].NameCode).ToHashSet();
    var definition = data.CharacterTable.Values.Where(c => c.Corporation == corporation && c.OriginalRare == rarity && c.GradeCoreId == grade && (!characterClass.HasValue || (int)c.Class == characterClass.Value) && !ownedNames.Contains(c.NameCode)).OrderBy(c => c.Id).First();
    var character = new CharacterModel { Csn = Player(id).Characters.Count + 1, Tid = definition.Id }; Player(id).Characters.Add(character); return character;
}
RecycleRoomResearchProgress Research(ulong id, RecycleType type, RecycleSubType subType, int level)
{
    int tid = data.RecycleResearchStats.Values.Single(r => r.RecycleType == type && r.RecycleSubType == subType).Id;
    return Player(id).ResearchProgress[tid] = new() { Level = level };
}
void FundSkill(ulong id)
{
    var c = data.CharacterTable[Player(id).Characters[0].Tid]; var cost = data.costTable[data.skillInfoTable[c.Skill1Id].LevelUpCostId];
    foreach (var item in cost.Costs.Where(c => c.ItemValue > 0))
        if (item.ItemType == RewardType.Item) Player(id).Items.Add(new() { Isn = Player(id).Items.Count + 1, ItemType = item.ItemId, Count = item.ItemValue });
        else if (item.ItemType == RewardType.Currency) Player(id).Currency[(CurrencyType)item.ItemId] = item.ItemValue;
}
void Target(int value) { data.Rankings.Clear(); data.Rankings[10201] = new() { Id = 10201, RankingType = RankingType.GroupPointElysion, ConditionValue = value, ServerRewardId = 15000001 }; }
Task<ResGetRankingList> Board(ulong id = self, int type = 2) => Send<ResGetRankingList>(id, "/ranking/getlist", new ReqGetRankingList { Type = type });
async Task FirstWinner() { var result = await Send<ResGetRankAchievementLeaderBoard>(self, "/ranking/rankachievement", new ReqGetRankAchievementLeaderBoard { Type = 2 }); Check(result.RankAchievement.Single(r => r.Rank == 1).User.Usn == (long)self, "first crossing was delayed until a later ranking query"); }
async Task Test(string name, Func<Task> test)
{
    try { instance.SetValue(null, Fixture()); using (var db = GameContext.CreateNew()) db.Triggers.ExecuteDelete(); Target(int.MaxValue); await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
async Task<T> Send<T>(ulong id, string route, IMessage request) where T : IMessage<T>, new()
{
    using var scope = services.CreateScope(); var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Request.Path = "/v1" + route; ctx.Request.Body = new MemoryStream(request.ToByteArray()); ctx.Response.Body = new MemoryStream();
    ctx.Request.Headers.Authorization = "Bearer " + new PasetoBuilder().Use(ProtocolVersion.V4, Purpose.Local).WithKey(key, Encryption.SymmetricKey).AddClaim("userId", id).Expiration(DateTime.UtcNow.AddMinutes(10)).Encode();
    await LobbyHandler.DispatchSingle(ctx); var response = new T(); response.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray()); return response;
}
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
