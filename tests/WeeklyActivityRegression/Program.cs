using System.Reflection;
using EpinelPS;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer;
using EpinelPS.LobbyServer.Simroom;
using EpinelPS.LobbyServer.Team;
using EpinelPS.Models;
using EpinelPS.Services;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(root, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
var connection = $"Data Source={Path.Combine(AppContext.BaseDirectory, "weekly-activity.db")}";
GameContext.InitializeOptions(connection, "sqlite");
var character = data.CharacterTable.Values.First(c => c.NameCode > 0 && c.OriginalRare == OriginalRareType.SSR);
var gift = data.itemMaterialTable.Values.First(m => m.ItemSubType == ItemSubType.AttractiveMaterial && m.ItemValue > 0 && m.MaterialType != MaterialType.Corporation && m.MaterialType != MaterialType.Squad);
var dispatch = data.DispatchTable.Values.First(d => d.RewardId > 0);
var tower = data.towerTable.Values.First(t => t.Type == CorporationTowerType.ALL && t.Floor == 1);
var users = Enumerable.Range(9300, 50).Select(id => new User
{
    ID = (ulong)id, InfraCoreLvl = 17, LastReset = DateTime.UtcNow, LastWeeklyReset = DateTime.UtcNow,
    Currency = Enum.GetValues<CurrencyType>().Distinct().ToDictionary(t => t, _ => 1_000_000_000L),
    UserDispatchData = new DispatchData { dispatchDatas = [] },
    Characters = [new CharacterModel { Csn = 1, Tid = character.Id, Grade = 3 }],
    BondInfo = [new NetUserAttractiveData { NameCode = character.NameCode, Lv = 1 }],
    Items = [new DbItemData { ItemType = gift.Id, Count = 8, Isn = 1 }]
}).ToList();
foreach (var u in users)
{
    var type = (int)SupportCharacterHelper.GetTowerTeamType(tower.Id);
    var team = new NetUserTeamData { Type = type };
    var first = new NetTeamData { TeamNumber = 1 };
    foreach (var slot in Enumerable.Range(1, 5)) first.Slots.Add(new NetTeamSlot { Slot = slot, Value = 1 });
    team.Teams.Add(first);
    u.UserTeams[type] = team;
}
File.WriteAllText(save, JsonConvert.SerializeObject(new CoreInfo { DbVersion = 5, BondRewardLevelsMigrated = true, Users = users }));
using (var db = GameContext.CreateNew())
{
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
    db.Users.AddRange(users.Select(u => new GameUser { ID = u.ID, Nickname = "Weekly" + u.ID }));
    db.SaveChanges();
}
var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
CoreInfo State() => (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
User Account(ulong id) => State().Users.Single(u => u.ID == id);
void Save() => jsonDb.GetMethod("Save")!.Invoke(null, null);
void Reload() => jsonDb.GetMethod("Reload")!.Invoke(null, null);
using var services = new ServiceCollection().AddDbContext<GameContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
int passed = 0, failed = 0;

await Test("weekly reset forgets previous claims and points durably", () =>
{
    var u = Account(9300);
    u.LastWeeklyReset = DateTime.UtcNow.AddDays(-8);
    u.WeeklyResetableData.CompletedWeeklyMissions.AddRange([20001, 20011]);
    u.WeeklyResetableData.WeeklyMissionPoints = 20;
    u.ResetDataIfNeeded();
    Reload();
    Check(Account(9300).WeeklyResetableData.CompletedWeeklyMissions.Count == 0 && Account(9300).WeeklyResetableData.WeeklyMissionPoints == 0,
        "the previous week's claimed IDs or points survived reset");
    return Task.CompletedTask;
});
await Test("an unfinished weekly task cannot award points", async () =>
{
    await IgnoreRejected(() => Claim(9301, 20001));
    Check(Account(9301).WeeklyResetableData.CompletedWeeklyMissions.Count == 0 && Points(9301) == 0 && Count(9301, Trigger.PointRewardWeekly) == 0,
        "a task with zero progress awarded weekly points");
});
await Test("the weekly endpoint rejects a daily task ID", async () =>
{
    Account(9302).AddTrigger(Trigger.GachaCharacter, 100);
    await IgnoreRejected(() => Claim(9302, 10001));
    Check(Account(9302).WeeklyResetableData.CompletedWeeklyMissions.Count == 0 && Points(9302) == 0, "the daily TID was accepted as a weekly claim");
});
await Test("last week's actions cannot complete this week's task", async () =>
{
    using (var db = GameContext.CreateNew())
    {
        db.Triggers.Add(new TriggerModelNew { UserId = 9303, Type = Trigger.GachaCharacter, Value = 20, CreatedAt = DateTime.UtcNow.AddDays(-8).AddHours(9).Ticks });
        db.SaveChanges();
    }
    await IgnoreRejected(() => Claim(9303, 20001));
    Check(Points(9303) == 0, "old progress completed a new weekly task");
});
await Test("current completed tasks and their unlocked point chest claim once", async () =>
{
    Account(9304).AddTrigger(Trigger.GachaCharacter, 20);
    await Claim(9304, 20001, 20011);
    string after = Inventory(Account(9304));
    await Claim(9304, 20001, 20011, 20001);
    Reload();
    Check(Points(9304) == 20 && Account(9304).WeeklyResetableData.CompletedWeeklyMissions.Order().SequenceEqual(new[] { 20001, 20011 }), "valid claims did not retain exactly one task and chest");
    Check(Count(9304, Trigger.PointRewardWeekly) == 20 && Inventory(Account(9304)) == after, "repeated claims duplicated points or inventory rewards");
});
await Test("insufficient weekly points cannot open the 100 point chest", async () =>
{
    string before = Inventory(Account(9305));
    await IgnoreRejected(() => Claim(9305, 20015));
    Check(!Account(9305).WeeklyResetableData.CompletedWeeklyMissions.Contains(20015) && Inventory(Account(9305)) == before,
        "an account with zero points received the 100 point chest");
});
await Test("a mixed valid and unfinished claim is rejected without partial rewards", async () =>
{
    Account(9306).AddTrigger(Trigger.GachaCharacter, 20);
    await IgnoreRejected(() => Claim(9306, 20001, 20002));
    Check(Points(9306) == 0 && Account(9306).WeeklyResetableData.CompletedWeeklyMissions.Count == 0 && Count(9306, Trigger.PointRewardWeekly) == 0,
        "the invalid batch partially awarded points");
});
await Test("a failed JSON weekly claim leaves no points or mission event", async () =>
{
    Account(9307).AddTrigger(Trigger.GachaCharacter, 20);
    await WithBrokenSave(() => Claim(9307, 20001));
    Check(Points(9307) == 0 && Account(9307).WeeklyResetableData.CompletedWeeklyMissions.Count == 0 && Count(9307, Trigger.PointRewardWeekly) == 0,
        "failed JSON save leaked a weekly claim or its event");
});
await Test("three successfully consumed gifts advance the gift mission by three", async () =>
{
    long before = DateTime.UtcNow.Ticks;
    await Present(9310, 3);
    var sync = await Send<ResSyncTrigger>(9310, "/trigger/sync", new ReqSyncTrigger());
    var events = sync.Triggers.Where(t => t.Trigger == (int)Trigger.CharacterAttractivePresent).ToArray();
    Check(Gifts(9310) == 5 && events.Sum(t => t.UserValue) == 3 && events.All(t => t.ConditionId == 0), "gift consumption was not counted by successful item quantity");
    Check(events.All(t => t.CreatedAt >= before && t.CreatedAt <= DateTime.UtcNow.Ticks), "gift mission timestamps are not UTC on the wire");
});
await Test("insufficient gift inventory cannot award affection or mission progress", async () =>
{
    string before = Inventory(Account(9311));
    await IgnoreRejected(() => Present(9311, 9));
    Check(Gifts(9311) == 8 && Inventory(Account(9311)) == before && Account(9311).BondInfo.Single().Exp == 0 && Count(9311, Trigger.CharacterAttractivePresent) == 0,
        "an excessive gift request consumed inventory, affection or progress");
});
await Test("negative gift quantity cannot increase inventory or count", async () =>
{
    await IgnoreRejected(() => Present(9312, -1));
    Check(Gifts(9312) == 8 && Account(9312).BondInfo.Single().Exp == 0 && Count(9312, Trigger.CharacterAttractivePresent) == 0, "a negative gift request mutated the account");
});
await Test("a failed gift JSON save rolls back inventory and mission progress", async () =>
{
    await WithBrokenSave(() => Present(9313, 3));
    Check(Gifts(9313) == 8 && Account(9313).BondInfo.Single().Exp == 0 && Count(9313, Trigger.CharacterAttractivePresent) == 0, "failed gift save leaked consumption or notifications");
});
await Test("gift notification delivery retries after SQLite is unavailable", async () =>
{
    using (var db = GameContext.CreateNew()) db.Database.ExecuteSqlRaw($"CREATE TRIGGER reject_gift BEFORE INSERT ON Triggers WHEN NEW.UserId = 9314 AND NEW.Type = {(int)Trigger.CharacterAttractivePresent} BEGIN SELECT RAISE(FAIL, 'test unavailable'); END;");
    try { await Present(9314, 3); }
    finally { using var db = GameContext.CreateNew(); db.Database.ExecuteSqlRaw("DROP TRIGGER reject_gift;"); }
    Check(Gifts(9314) == 5 && Account(9314).PendingMissionTriggers.Sum(t => t.Type == Trigger.CharacterAttractivePresent ? t.Value : 0) == 3,
        "successful gift consumption lost its durable pending notification");
    Reload();
    await Send<ResSyncTrigger>(9314, "/trigger/sync", new ReqSyncTrigger());
    await Send<ResSyncTrigger>(9314, "/trigger/sync", new ReqSyncTrigger());
    Check(Count(9314, Trigger.CharacterAttractivePresent) == 3 && Account(9314).PendingMissionTriggers.Count == 0, "gift outbox retry lost or duplicated progress");
});
await Test("merely generating simulation events cannot count as a clear", async () =>
{
    await Send<ResSelectSimRoomDifficulty>(9320, "/simroom/selectdifficulty", new ReqSelectSimRoomDifficulty { Difficulty = 1, StartingChapter = 1 });
    Check(Count(9320, Trigger.SimulationRoomClearWithoutCondition) == 0, "starting a simulation was counted as clearing it");
});
await Test("settling the completed simulation boss counts once across repeated reward selection", async () =>
{
    var req = ReadySimulationBoss(9321);
    await Send<ResSelectSimRoomBuff>(9321, "/simroom/selectbuff", req);
    string after = Inventory(Account(9321));
    await Send<ResSelectSimRoomBuff>(9321, "/simroom/selectbuff", req);
    Reload();
    Check(Count(9321, Trigger.SimulationRoomClearWithoutCondition) == 1 && Inventory(Account(9321)) == after, "boss reward settlement lost the clear or repeated it");
});
await Test("uncompleted simulation boss cannot claim the final reward", async () =>
{
    var req = ReadySimulationBoss(9322);
    Account(9322).ResetableData.SimRoomData.Events.Single(e => e.Location.Stage == req.Location.Stage && e.Location.Order == req.Location.Order).Battle.Progress = 0;
    string before = Inventory(Account(9322));
    await IgnoreRejected(() => Send<ResSelectSimRoomBuff>(9322, "/simroom/selectbuff", req));
    Check(Count(9322, Trigger.SimulationRoomClearWithoutCondition) == 0 && Account(9322).ResetableData.SimRoomData.ReceivedRewardChapters.Count == 0 && Inventory(Account(9322)) == before,
        "reward selection without a completed battle credited a simulation clear");
});
await Test("successful simple-mode skip counts once when its reward is committed", async () =>
{
    Account(9323).ResetableData.SimRoomData.IsSimpleModeSkipEnabled = true;
    await Send<ResSkipAllSimRoomSimpleMode>(9323, "/simroom/simplemode/skipall", new ReqSkipAllSimRoomSimpleMode());
    string after = Inventory(Account(9323));
    await Send<ResSkipAllSimRoomSimpleMode>(9323, "/simroom/simplemode/skipall", new ReqSkipAllSimRoomSimpleMode());
    Check(Count(9323, Trigger.SimulationRoomClearWithoutCondition) == 1 && Inventory(Account(9323)) == after, "simple-mode skip did not count exactly its first successful reward");
});
await Test("a failed simulation reward save cannot leave a mission clear", async () =>
{
    var req = ReadySimulationBoss(9324);
    string before = Inventory(Account(9324));
    await WithBrokenSave(() => Send<ResSelectSimRoomBuff>(9324, "/simroom/selectbuff", req));
    Check(Count(9324, Trigger.SimulationRoomClearWithoutCondition) == 0 && Account(9324).ResetableData.SimRoomData.ReceivedRewardChapters.Count == 0 && Inventory(Account(9324)) == before,
        "failed simulation settlement leaked rewards or a clear");
});
await Test("a delayed battle-clear retry cannot reopen the already settled simulation boss", async () =>
{
    var req = ReadySimulationBoss(9325);
    await Send<ResSelectSimRoomBuff>(9325, "/simroom/selectbuff", req);
    var retry = await Send<ResClearSimRoomBattle>(9325, "/simroom/clearbattle", new ReqClearSimRoomBattle
    { Location = req.Location, Event = req.Event, TeamNumber = 1, BattleResult = 1 });
    Check(Account(9325).ResetableData.SimRoomData.Events.Single(e => e.Location.Stage == req.Location.Stage && e.Location.Order == req.Location.Order).Battle.Progress == (int)SimRoomBattleEventProgress.RewardReceived,
        "a late clearbattle retry moved the settled boss back to RewardWaiting");
    if (retry.BuffOptions.Count > 0) req.BuffToAdd = retry.BuffOptions[0];
    await IgnoreRejected(() => Send<ResSelectSimRoomBuff>(9325, "/simroom/selectbuff", req));
    Check(Count(9325, Trigger.SimulationRoomClearWithoutCondition) == 1,
        "a late clearbattle retry reopened a received boss reward and counted another clear");
});
await Test("simple-mode start from default state persists difficulty 5 chapter 3 and settles a real boss", async () =>
{
    var start = await Send<ResStartSimRoomSimpleMode>(9326, "/simroom/simplemode/start", new ReqStartSimRoomSimpleMode());
    Reload();
    var sim = Account(9326).ResetableData.SimRoomData;
    Check(sim.CurrentDifficulty == 5 && sim.CurrentChapter == 3, "simple-mode start left the persisted difficulty or chapter at zero");
    Check(Count(9326, Trigger.SimulationRoomClearWithoutCondition) == 0, "simple-mode start itself counted a clear");
    var settled = await CompleteSimpleSimulation(9326, start);
    Check(settled.Reward != null && Account(9326).ResetableData.SimRoomData.ReceivedRewardChapters.Any(c => c.Difficulty == 5 && c.Chapter == 3) &&
        Count(9326, Trigger.SimulationRoomClearWithoutCondition) == 1, "the real simple-mode boss failed to award its difficulty 5 chapter 3 reward and one clear");
});
await Test("switching a prior difficulty 1 chapter 1 simulation to simple mode replaces old events", async () =>
{
    await Send<ResSelectSimRoomDifficulty>(9327, "/simroom/selectdifficulty", new ReqSelectSimRoomDifficulty { Difficulty = 1, StartingChapter = 1 });
    var start = await Send<ResStartSimRoomSimpleMode>(9327, "/simroom/simplemode/start", new ReqStartSimRoomSimpleMode());
    Check(start.Events.Count > 0 && start.Events.All(e => e.Location.Chapter == 3), "simple mode reused old chapter 1 events instead of generating chapter 3");
    Reload();
    Check(Account(9327).ResetableData.SimRoomData.CurrentDifficulty == 5 && Account(9327).ResetableData.SimRoomData.CurrentChapter == 3 &&
        Account(9327).ResetableData.SimRoomData.Events.All(e => e.Location.Chapter == 3), "simple-mode state and the generated chapter disagree after reload");
    var settled = await CompleteSimpleSimulation(9327, start);
    Check(settled.Reward != null && Count(9327, Trigger.SimulationRoomClearWithoutCondition) == 1 &&
        Account(9327).ResetableData.SimRoomData.ReceivedRewardChapters.Any(c => c.Difficulty == 5 && c.Chapter == 3), "replaced simple-mode events did not settle their own chapter reward exactly once");
});
await Test("a long-online account can complete a fresh simple-mode simulation after the daily boundary", async () =>
{
    YesterdaySimulationReward(9328);
    var start = await Send<ResStartSimRoomSimpleMode>(9328, "/simroom/simplemode/start", new ReqStartSimRoomSimpleMode());
    var settled = await CompleteSimpleSimulation(9328, start);
    Check(settled.Reward != null && Count(9328, Trigger.SimulationRoomClearWithoutCondition) == 1 && Account(9328).LastReset > DateTime.UtcNow.AddMinutes(-1),
        "direct simple-mode start retained yesterday's reward state instead of resetting the daily boundary");
    string after = Inventory(Account(9328));
    var repeated = await Send<ResStartSimRoomSimpleMode>(9328, "/simroom/simplemode/start", new ReqStartSimRoomSimpleMode());
    var replay = await CompleteSimpleSimulation(9328, repeated);
    Check(replay.Reward == null && Count(9328, Trigger.SimulationRoomClearWithoutCondition) == 1 && Inventory(Account(9328)) == after,
        "same-day simple-mode retry added another reward or clear");
});
await Test("a long-online account can skip a fresh daily simulation once after the boundary", async () =>
{
    YesterdaySimulationReward(9329);
    var first = await Send<ResSkipAllSimRoomSimpleMode>(9329, "/simroom/simplemode/skipall", new ReqSkipAllSimRoomSimpleMode());
    Check(first.Reward != null && Count(9329, Trigger.SimulationRoomClearWithoutCondition) == 1 && Account(9329).LastReset > DateTime.UtcNow.AddMinutes(-1),
        "direct simple-mode skip retained yesterday's claimed reward and lost today's clear");
    string after = Inventory(Account(9329));
    Reload();
    var replay = await Send<ResSkipAllSimRoomSimpleMode>(9329, "/simroom/simplemode/skipall", new ReqSkipAllSimRoomSimpleMode());
    Check(replay.Reward == null && Count(9329, Trigger.SimulationRoomClearWithoutCondition) == 1 && Inventory(Account(9329)) == after,
        "same-day skip replay added another reward or clear");
});
await Test("a finished dispatch counts only once for duplicate IDs and request replay", async () =>
{
    AddDispatch(9330, 1, DateTime.UtcNow.AddMinutes(-1));
    await Dispatch(9330, dispatch.Id, dispatch.Id);
    string after = Inventory(Account(9330));
    await IgnoreRejected(() => Dispatch(9330, dispatch.Id));
    Check(Count(9330, Trigger.SendDispatch) == 1 && Inventory(Account(9330)) == after && Account(9330).UserDispatchData.dispatchDatas.Count == 0,
        "a dispatch reward ID was paid or counted repeatedly");
});
await Test("a running dispatch cannot finish before its end time", async () =>
{
    AddDispatch(9331, 1, DateTime.UtcNow.AddHours(1));
    string before = Inventory(Account(9331));
    await IgnoreRejected(() => Dispatch(9331, dispatch.Id));
    Check(Count(9331, Trigger.SendDispatch) == 0 && Inventory(Account(9331)) == before && Account(9331).UserDispatchData.dispatchDatas.Count == 1, "an unfinished dispatch paid rewards or progress");
});
await Test("an unstarted dispatch cannot award completion progress", async () =>
{
    AddDispatch(9332, 0, DateTime.UtcNow.AddMinutes(-1));
    await IgnoreRejected(() => Dispatch(9332, dispatch.Id));
    Check(Count(9332, Trigger.SendDispatch) == 0 && Account(9332).UserDispatchData.dispatchDatas.Count == 1, "an unstarted dispatch was completed");
});
await Test("a static dispatch ID absent from the account cannot award progress", async () =>
{
    string before = Inventory(Account(9333));
    await IgnoreRejected(() => Dispatch(9333, dispatch.Id));
    Check(Count(9333, Trigger.SendDispatch) == 0 && Inventory(Account(9333)) == before, "a nonexistent dispatch was paid");
});
await Test("a failed dispatch save leaves the running record claimable", async () =>
{
    AddDispatch(9334, 1, DateTime.UtcNow.AddMinutes(-1));
    string before = Inventory(Account(9334));
    await WithBrokenSave(() => Dispatch(9334, dispatch.Id));
    Check(Count(9334, Trigger.SendDispatch) == 0 && Account(9334).UserDispatchData.dispatchDatas.Count == 1 && Inventory(Account(9334)) == before,
        "failed dispatch save consumed the record or left progress");
});
await Test("the same tower-entry nonce counts once even after save reload", async () =>
{
    var req = new ReqEnterTower { TowerId = tower.Id, TeamNumber = 1 };
    await Send<ResEnterTower>(9340, "/tower/entertower", req, "tower-one");
    Reload();
    await Send<ResEnterTower>(9340, "/tower/entertower", req, "tower-one");
    Check(Count(9340, Trigger.TowerAllStart) == 1, "tower entry retransmission was counted as a second attempt");
    await Send<ResEnterTower>(9340, "/tower/entertower", req, "tower-two");
    Check(Count(9340, Trigger.TowerAllStart) == 2, "a distinct accepted tower entry was not counted");
});
await Test("an unknown tower cannot advance the weekly task", async () =>
{
    await IgnoreRejected(() => Send<ResEnterTower>(9341, "/tower/entertower", new ReqEnterTower { TowerId = int.MaxValue, TeamNumber = 1 }, "bad-tower"));
    Check(Count(9341, Trigger.TowerAllStart) == 0, "an invalid tower awarded entry progress");
});
await Test("a failed tower-entry save leaves no event and can retry", async () =>
{
    var req = new ReqEnterTower { TowerId = tower.Id, TeamNumber = 1 };
    await WithBrokenSave(() => Send<ResEnterTower>(9342, "/tower/entertower", req, "tower-save"));
    Check(Count(9342, Trigger.TowerAllStart) == 0, "failed tower entry leaked a mission event");
    await Send<ResEnterTower>(9342, "/tower/entertower", req, "tower-save");
    Check(Count(9342, Trigger.TowerAllStart) == 1, "failed entry permanently consumed its retry identity");
});

Save();
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

Task Claim(ulong id, params int[] tids) => Send<ResObtainWeeklyMissionReward>(id, "/mission/obtain/weekly", new ReqObtainWeeklyMissionReward { TidList = { tids } });
Task Present(ulong id, int count) => Send<ResCharacterPresent>(id, "/character/attractive/present", new ReqCharacterPresent
{ NameCode = character.NameCode, Items = { new NetItemData { Tid = gift.Id, Count = count } } });
Task Dispatch(ulong id, params int[] tids) => Send<ResObtainDispatchReward>(id, "/outpost/obtaindispatchreward", new ReqObtainDispatchReward { TidList = { tids } });
int Points(ulong id) => Account(id).WeeklyResetableData.WeeklyMissionPoints;
int Gifts(ulong id) => Account(id).Items.Where(i => i.ItemType == gift.Id).Sum(i => i.Count);
int Count(ulong id, Trigger type)
{
    using var db = GameContext.CreateNew();
    return db.Triggers.Where(t => t.UserId == id && t.Type == type).Select(t => t.Value).ToArray().Sum();
}
string Inventory(User u) => JsonConvert.SerializeObject(new { u.Items, u.Currency, u.Characters });
void YesterdaySimulationReward(ulong id)
{
    var u = Account(id);
    u.LastReset = DateTime.UtcNow.AddDays(-1);
    u.ResetableData.SimRoomData.CurrentDifficulty = 5;
    u.ResetableData.SimRoomData.CurrentChapter = 3;
    u.ResetableData.SimRoomData.IsSimpleModeSkipEnabled = true;
    u.ResetableData.SimRoomData.ReceivedRewardChapters.Add(new SimRoomChapterInfo { Difficulty = 5, Chapter = 3 });
    Save();
}
async Task<ResSelectSimRoomBuff> CompleteSimpleSimulation(ulong id, ResStartSimRoomSimpleMode start)
{
    var boss = start.Events.Where(e => e.EventCase == NetSimRoomEvent.EventOneofCase.Battle).OrderBy(e => e.Location.Stage).ThenBy(e => e.Location.Order).Last();
    Check(data.SimulationRoomBattleEventTable[boss.Battle.Id].EventType == SimulationRoomEvent.BossBattle && boss.Location.Chapter == 3,
        "simple-mode response did not contain a real final chapter 3 boss");
    var cleared = await Send<ResClearSimRoomBattle>(id, "/simroom/clearbattle", new ReqClearSimRoomBattle
    { Location = boss.Location, Event = boss.Battle.Id, TeamNumber = 1, BattleResult = 1 });
    Check(cleared.BuffOptions.Count > 0, "completed simple-mode boss has no offered reward selection");
    var req = new ReqSelectSimRoomBuff { Location = boss.Location, Event = boss.Battle.Id, BuffToAdd = cleared.BuffOptions[0] };
    var settled = await Send<ResSelectSimRoomBuff>(id, "/simroom/selectbuff", req);
    await Send<ResSelectSimRoomBuff>(id, "/simroom/selectbuff", req);
    return settled;
}
void AddDispatch(ulong id, int run, DateTime end)
{
    Account(id).UserDispatchData.dispatchDatas.Add(new NetUserDispatchData { Tid = dispatch.Id, IsRun = run, StartAt = DateTime.UtcNow.AddHours(-2).Ticks, EndAt = end.Ticks });
    Save();
}
ReqSelectSimRoomBuff ReadySimulationBoss(ulong id)
{
    var u = Account(id);
    u.ResetableData.SimRoomData.Entered = true;
    u.ResetableData.SimRoomData.CurrentDifficulty = 1;
    u.ResetableData.SimRoomData.CurrentChapter = 1;
    SimRoomHelper.GetSimRoomEvents(u, 1, 1);
    var events = u.ResetableData.SimRoomData.Events;
    var last = events.OrderBy(e => e.Location.Stage).ThenBy(e => e.Location.Order).Last();
    foreach (var e in events.Where(e => !ReferenceEquals(e, last)))
    {
        e.Battle.Progress = (int)SimRoomBattleEventProgress.RewardReceived;
        foreach (var group in e.Selection.Group) group.IsDone = true;
    }
    var location = new NetSimRoomEventLocationInfo { Chapter = last.Location.Chapter, Stage = last.Location.Stage, Order = last.Location.Order };
    var options = SimRoomHelper.GetBuffOptions(u, location);
    Check(options.Count > 0, "real simulation boss has no buff options");
    // Isolate settlement from the separately tested premature-clear bug in event generation.
    using (var db = GameContext.CreateNew())
    {
        db.Triggers.RemoveRange(db.Triggers.Where(t => t.UserId == id && t.Type == Trigger.SimulationRoomClearWithoutCondition));
        db.SaveChanges();
    }
    Save();
    return new ReqSelectSimRoomBuff { Location = location, Event = last.Battle.Id, BuffToAdd = options[0] };
}
async Task<T> Send<T>(ulong id, string route, IMessage request, string? nonce = null) where T : IMessage<T>, new()
{
    using var scope = services.CreateScope();
    var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    ctx.Items["UserID"] = id;
    if (nonce != null) ctx.Items["Nonce"] = System.Text.Encoding.UTF8.GetBytes(nonce);
    ctx.Request.Path = "/v1" + route;
    ctx.Request.Body = new MemoryStream(request.ToByteArray());
    ctx.Response.Body = new MemoryStream();
    await LobbyHandler.DispatchSingle(ctx);
    var response = new T();
    response.MergeFrom(((MemoryStream)ctx.Response.Body).ToArray());
    return response;
}
async Task IgnoreRejected(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is BadHttpRequestException or InvalidOperationException or ArgumentException) { }
}
async Task WithBrokenSave(Func<Task> action)
{
    var temp = Path.Combine(AppContext.BaseDirectory, "db.json.tmp");
    Directory.CreateDirectory(temp);
    bool rejected = false;
    try
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
    }
    finally { Directory.Delete(temp); }
    Check(rejected, "blocked JSON save did not reject the operation");
}
async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}
