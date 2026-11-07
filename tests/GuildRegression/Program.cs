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
using Newtonsoft.Json.Linq;

const ulong founder = 901, other = 902;
const string name = "曙光";
const string replayNonce = "000102030405060708090A0B0C0D0E0F1011121314151617";
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../.."));
var pack = Directory.GetFiles(Path.Combine(workspace, "Server and Server selector/cache"), "StaticData.pack", SearchOption.AllDirectories).Single();
var data = new GameData(pack);
typeof(GameData).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, data);
await GameDataInitializer.InitializeGameData(new Progress<double>());
var conn = $"Data Source={Path.Combine(AppContext.BaseDirectory, "guild-regression.db")}";
GameContext.InitializeOptions(conn, "sqlite");
var save = Path.Combine(AppContext.BaseDirectory, "db.json");
bool restarting = args.Contains("--verify-saved");
if (!restarting)
{
    var users = new[] { Account(founder, 10, 900), Account(other, 10, 900), Account(903, 9, 900),
        Account(904, 10, 499), Account(905, 10, 250, 250), Account(906, 10, 0, 500),
        Account(907, 10, 900), Account(908, 10, 900), Account(909, 10, 900), Account(910, 10, 500),
        Account(911, 10, 800), Account(912, 10, 800), Account(913, 10, 800),
        Account(940, 10, 900), Account(941, 10, 900),
        Account(950, 10, 900), Account(951, 10, 900), Account(952, 10, 900),
        Account(953, 10, 900), Account(954, 10, 900) }
        .Concat(Enumerable.Range(920, 12).Select(i => {
            var u = Account((ulong)i, 10, 500);
            for (int n = 1; n <= 5; n++) u.SynchroSlots.Add(new SynchroSlot { Slot = n });
            return u;
        })).ToArray();
    // An old save has users and wallets, but no guild registry.
    var legacySave = JObject.FromObject(new { DbVersion = 5, Users = users });
    legacySave["Users"]![1]!["Guild"] = JValue.CreateNull();
    File.WriteAllText(save, legacySave.ToString());
    using var db = GameContext.CreateNew();
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
    db.Users.AddRange(users.Select(u => new GameUser { ID = u.ID, Nickname = $"P{u.ID}", Description = $"Bio{u.ID}", LastAction = DateTime.UtcNow }));
    db.SaveChanges();
}
else
{
    // Reproduce a receipt saved by the original guild server before the client reconnected.
    var snapshot = JObject.Parse(File.ReadAllText(save));
    var guild = snapshot["Guilds"]![Saved(913).Guild.guildId!.Value.ToString()]!;
    var receipt = ResCreateGuild.Parser.ParseFrom(Convert.FromBase64String((string)guild["CreationReceipt"]!));
    receipt.Members[0].SendMailAt = null;
    receipt.Members[0].UserTitleDisplayData = null;
    guild["CreationReceipt"] = Convert.ToBase64String(receipt.ToByteArray());
    File.WriteAllText(save, snapshot.ToString());
}
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.AllowSynchronousIO = true; });
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddDbContext<GameContext>(o => o.UseSqlite(conn));
builder.Services.AddControllers(o => o.OutputFormatters.Insert(0, new ProtobufOutputFormatter()))
    .AddApplicationPart(typeof(LobbyHandler).Assembly);
await using var app = builder.Build();
app.Use(async (ctx, next) => {
    if (ulong.TryParse(ctx.Request.Headers["Test-User"], out var id)) ctx.Items["UserID"] = id;
    if (ctx.Request.Headers.TryGetValue("Test-Nonce", out var nonce)) ctx.Items["Nonce"] = Convert.FromHexString(nonce.ToString());
    await next();
});
app.MapControllers();
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/v1/") };
var failures = new List<string>();
if (!restarting)
{
    await Test("a legacy account with null guild data can still open its profile and union list", async () => {
        var account = await Read(other, "User/Get", new ReqGetUserData(), ResGetUserData.Parser);
        var list = await Read(other, "guild/recommendlist", new ReqRecommendGuildList(), ResRecommendGuildList.Parser);
        Check(account.Gsn == 0 && list.Guilds.Count == 0, "legacy account has a phantom guild");
    });
    await Test("creator receives the guild, founder profile and final balance", async () => {
        var r = await Create(founder, name);
        Check(r.Result == CreateGuildResult.Okay && r.Guild?.Gsn > 0, "missing created guild");
        Check(r.Guild.Name == name && r.Guild.Emblem == 1 && r.Guild.Locale == "zh-TW" &&
              r.Guild.Grade == 1 && r.Guild.JoinLevel == 10, "wrong guild defaults");
        var m = r.Members.Single();
        Check(m.Usn == (long)founder && m.Nickname == "P901" && m.Lv == 10 && m.JoinedAt > 0, "wrong founder");
        Check(m.SendMailAt?.ToDateTime() == DateTime.UnixEpoch && m.UserTitleDisplayData?.Count == 0,
              "new founder has no readable mail timestamp or title display");
        Check(r.Currencies.Single(c => c.Type == 99).Value == 400 && Saved(founder).Currency[CurrencyType.FreeCash] == 400, "fee not settled");
    });
    await Test("replay and another account's duplicate name never charge", async () => {
        Check((await Create(founder, name)).Result == CreateGuildResult.AlreadyJoin, "creator replay");
        Check((await Create(other, name)).Result == CreateGuildResult.ExistName, "name conflict");
        Check(Saved(founder).Currency[CurrencyType.FreeCash] == 400 && Saved(other).Currency[CurrencyType.FreeCash] == 900, "charged twice");
    });
    await Test("level, wallet, name and authentication failures leave no membership", async () => {
        foreach (ulong id in new ulong[] { 903, 904, 0 })
        {
            using var r = await Send(id, "guild/create", Request("星光"));
            Check(!r.IsSuccessStatusCode, $"ineligible {id} got success");
        }
        foreach (string invalid in new[] { "  ", "一", "1234", "A B", "星★", "abcdefghi", "ㄱㄱ" })
            Check((await Create(other, invalid)).Result == CreateGuildResult.Inappropriate, $"invalid name accepted: {invalid}");
        foreach (ulong id in new ulong[] { other, 903, 904 })
            Check(!(Saved(id).Guild.guildId > 0) && Saved(id).Currency[CurrencyType.FreeCash] == (id == 904 ? 499 : 900), "invalid request modified save");
    });
    await Test("free gems are used first, including exact mixed and paid-only balances", async () => {
        var split = await Create(905, "混合");
        Check(split.Guild != null && split.Currencies.Single(c => c.Type == 99).Value == 0 &&
              split.Currencies.Single(c => c.Type == 98).Value == 0, "mixed wallet");
        var paid = await Create(906, "付費");
        Check(paid.Guild != null && paid.Currencies.Single(c => c.Type == 98).Value == 0, "paid-only wallet");
    });
    await Test("emblems use unlocked table IDs and names are unique across letter case", async () => {
        var request = Request("StarTeam");
        request.Emblem = 3;
        using var failed = await Send(911, "guild/create", request);
        Check(!failed.IsSuccessStatusCode && Saved(911).Currency[CurrencyType.FreeCash] == 800, "locked emblem was accepted");
        request.Emblem = 2001;
        var guild = await Read(911, "guild/create", request, ResCreateGuild.Parser);
        Check(guild.Guild?.Emblem == 2001 && (await Create(912, "STARTEAM")).Result == CreateGuildResult.ExistName, "emblem or case conflict");
        request.Name = "預設";
        request.Emblem = 0;
        Check((await Read(912, "guild/create", request, ResCreateGuild.Parser)).Guild?.Emblem == 1, "default emblem missing");
    });
    await Test("concurrent name claims and double clicks have one winner", async () => {
        var attempts = await Task.WhenAll(Create(907, "同行"), Create(908, "同行"));
        Check(attempts.Count(r => r.Guild != null) == 1 && attempts.Count(r => r.Result == CreateGuildResult.ExistName) == 1, "name race");
        var clicks = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Create(909, "重試")));
        Check(clicks.Count(r => r.Guild != null) == 1 && clicks.Count(r => r.Result == CreateGuildResult.AlreadyJoin) == 4, "double click");
        Check(Saved(907).Currency[CurrencyType.FreeCash] + Saved(908).Currency[CurrencyType.FreeCash] == 1300 &&
              Saved(909).Currency[CurrencyType.FreeCash] == 400, "race charged extra");
    });
    await Test("a paid synchro slot and guild creation cannot spend the same 500 gems", async () => {
        for (ulong id = 920; id < 932; id++)
        {
            var create = Send(id, "guild/create", Request($"星{id}"));
            var slot = Read(id, "character/synchrodevice/addslot", new ReqSynchroAddSlot { Slot = 6 }, ResSynchroAddSlot.Parser);
            using var result = await create;
            var purchased = (await slot).Slot?.Slot == 6;
            var account = Saved(id);
            Check(result.IsSuccessStatusCode != purchased && (account.Guild.guildId > 0) != (account.SynchroSlots.Count == 6) &&
                  account.Currency[CurrencyType.FreeCash] == 0, $"balance or purchases wrong for {id}");
        }
    });
    await Test("a lost reply can be replayed with its original transport nonce", async () => {
        using var first = await Send(913, "guild/create", Request("封包"), replayNonce);
        var created = ResCreateGuild.Parser.ParseFrom(await first.Content.ReadAsByteArrayAsync());
        Check(created.Guild != null, "initial receipt missing");
        using var replay = await Send(913, "guild/create", Request("封包"), replayNonce);
        Check(created.Equals(ResCreateGuild.Parser.ParseFrom(await replay.Content.ReadAsByteArrayAsync())) &&
              Saved(913).Currency[CurrencyType.FreeCash] == 300, "retry lost original receipt");
        using var altered = await Send(913, "guild/create", Request("改名"), replayNonce);
        Check(ResCreateGuild.Parser.ParseFrom(await altered.Content.ReadAsByteArrayAsync()).Result == CreateGuildResult.AlreadyJoin, "nonce reused for another body");
    });
    await Test("an interrupted save rolls back the fee, membership and reserved name before retry", async () => {
        var temp = Directory.CreateDirectory(save + ".tmp");
        try
        {
            using var failed = await Send(910, "guild/create", Request("復原"));
            Check(!failed.IsSuccessStatusCode, "failed save was acknowledged");
        }
        finally { temp.Delete(); }
        var user = await Read(910, "User/Get", new ReqGetUserData(), ResGetUserData.Parser);
        Check(user.Gsn == 0 && user.Currency.Single(c => c.Type == 99).Value == 500, "failed save leaked into memory");
        Check(Saved(910).Guild.guildId is null or 0 && (await Create(910, "復原")).Guild != null, "retry did not recover");
    });
}
if (restarting) await Test("the original creation receipt survives a process restart", async () => {
    using var replay = await Send(913, "guild/create", Request("封包"), replayNonce);
    var r = ResCreateGuild.Parser.ParseFrom(await replay.Content.ReadAsByteArrayAsync());
    Check(r.Guild?.Gsn == Saved(913).Guild.guildId && r.Members.Single().Usn == 913 && r.Currencies.Single().Value == 300, "saved receipt missing");
    Check(r.Members[0].SendMailAt != null && r.Members[0].UserTitleDisplayData != null &&
          Saved(913).Currency[CurrencyType.FreeCash] == 300, "legacy receipt replay lost member details or charged again");
});
await Test("login, account refresh and reopening the guild recover the same membership", async () => {
    var login = await Read(founder, "enterlobbyserver", new ReqEnterLobbyServer(), ResEnterLobbyServer.Parser);
    Check(login.Gsn > 0 && login.Gsn == Saved(founder).Guild.guildId, "login has no guild ID");
    var user = await Read(founder, "User/Get", new ReqGetUserData(), ResGetUserData.Parser);
    Check(user.Gsn == login.Gsn && user.Currency.Single(c => c.Type == 99).Value == 400, "refresh lost guild/fee");
    var own = await Read(founder, "guild/get", new ReqGetGuild { Gsn = login.Gsn }, ResGetGuild.Parser);
    Check(own.Guild?.Name == name && own.Members.Single().Usn == (long)founder, "could not reopen guild");
    var simple = await Read(founder, "guild/get", new ReqGetGuild { Gsn = login.Gsn, IsSimple = true }, ResGetGuild.Parser);
    Check(simple.IsSimple && simple.Guild?.Gsn == login.Gsn, "simple refresh lost guild");
    Check(simple.Members.Single().SendMailAt != null && simple.Members.Single().UserTitleDisplayData != null,
          "reopened guild roster has incomplete member data");
    var contents = await Read(founder, "user/getcontentsdata", new ReqGetContentsOpenData(), ResGetContentsOpenData.Parser);
    Check(contents.GuildLevel == 1, "content unlock grade missing");
});
await Test("another user can discover the guild by name or ID and view the founder", async () => {
    var list = await Read(other, "guild/recommendlist", new ReqRecommendGuildList(), ResRecommendGuildList.Parser);
    var guild = list.Guilds.Single(g => g.Name == name);
    Check(guild.MemberCount == 1 && guild.Grade == 1 && guild.Emblem == 1 && guild.Gsn > 0, "bad recommendation");
    foreach (string search in new[] { name, guild.Gsn.ToString(), "不存在" })
    {
        var r = await Read(other, "guild/search", new ReqSearchGuildList { Search = search }, ResSearchGuildList.Parser);
        Check(search == "不存在" ? r.Guilds.Count == 0 : r.Guilds.Single().Gsn == guild.Gsn, "wrong search result");
    }
    var info = await Read(other, "guild/publicinfo", new ReqGetGuildPublicInfo { Gsn = guild.Gsn }, ResGetGuildPublicInfo.Parser);
    Check(info.Guild?.Gsn == guild.Gsn && info.Members.Single().Nickname == "P901", "public info missing");
    var profile = await Read(other, "User/GetProfile", new ReqGetProfileData { TargetUsn = (long)founder }, ResGetProfileData.Parser);
    Check(profile.Data.Guild?.Gsn == guild.Gsn && profile.Data.User.GuildName == name && profile.Data.Desc == "Bio901", "viewer's data leaked into founder profile");
    var outsider = await Read(other, "User/Get", new ReqGetUserData(), ResGetUserData.Parser);
    Check(outsider.Gsn == 0 && outsider.Currency.Single(c => c.Type == 99).Value == 900, "outsider changed");
});
await Test("a new grade-one guild has an inactive raid with no attempts used", async () => {
    long id = Saved(founder).Guild.guildId ?? 0;
    foreach (bool simple in new[] { true, false })
    {
        var raid = await Read(founder, "guild/unionraid/get", new ReqGetUnionRaidData { Gsn = id, IsSimple = simple }, ResGetUnionRaidData.Parser);
        Check(raid.Data?.UnionRaidState == UnionRaidState.Disable && raid.Data.CurrentStatus?.Gsn == id &&
              raid.Data.UnionRaidJoinData?.PlayCount == 0 && raid.Data.PeriodData != null, "missing locked raid state");
        Check(raid.Data.CurrentStatus.HasLeftHp && raid.Data.CurrentStatus.LeftHp == 0,
              "client cannot select a normal raid progress case for the inactive boss");
    }
    var attempts = await Read(founder, "guild/unionraid/getjoindata", new ReqGetUnionRaidJoinData(), ResGetUnionRaidJoinData.Parser);
    Check(attempts.UnionRaidJoinData?.PlayCount == 0, "attempts missing");
});
await Test("the guild hall can look up one unranked board per element and battle length", async () => {
    var stages = await data.LoadZip<ShootingRangeRecord>("ShootingRangeTable.json", new Progress<double>());
    var stagesById = stages.ToDictionary(s => s.Id);
    var expected = stages.Select(s => (s.ShootingRangeType, s.ShootingRangeBattleLengthType)).ToHashSet();
    Check(expected.Contains((ShootingRangeType.Fire, ShootingRangeBattleLength.Short)), "missing Fire/Short fixture");
    var rankings = await Read(founder, "shootingrange/v2/get", new ReqGetShootingRangeRankingV2(), ResGetShootingRangeRankingV2.Parser);
    var boards = rankings.RankingList.ToDictionary(r => {
        var stage = stagesById[r.ShootingRangeId];
        Check(!stage.SpecActiveSpot, "fixed-spec practice duplicated a ranking category");
        return (stage.ShootingRangeType, stage.ShootingRangeBattleLengthType);
    });
    Check(rankings.Result == ShootingRangeRankingResult.GetShootingRangeRankingResultSuccess &&
          expected.SetEquals(boards.Keys), $"received {boards.Count} of {expected.Count} range categories");
    foreach (var (category, board) in boards)
        Check(board.GuildRankingList.Count == 0 && board.UserGuildRanking is { Rank: 0, Score: 0 } personal &&
              personal.User?.Usn == (long)founder && personal.User.UserTitleDisplayData != null, $"invalid unranked board for {category}");
});
await Test("shooting range rankings require a session and guild membership", async () => {
    var request = new ReqGetShootingRangeRankingV2();
    var outside = await Read(other, "shootingrange/v2/get", request, ResGetShootingRangeRankingV2.Parser);
    Check(outside.Result == ShootingRangeRankingResult.GetShootingRangeRankingResultNotMember &&
          outside.RankingList.Count == 0, "an outsider received guild rankings");
    using var anonymous = await Send(0, "shootingrange/v2/get", request);
    Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "anonymous ranking request accepted");
});
await Test("guild hall journal, chat roster and application list are scoped to members", async () => {
    long id = Saved(founder).Guild.guildId ?? 0;
    var journal = await Read(founder, "guild/getlog", new ReqGetGuildLog { Gsn = id }, ResGetGuildLog.Parser);
    var entry = journal.Logs.Single();
    Check(entry.Id == "GUILD_JOURNAL_GUILD_CREATE" && entry.Arguments.Single().Value == "P901" && entry.CreatedAt > 0, "founding journal entry lost");
    var foreignLog = await Read(other, "guild/getlog", new ReqGetGuildLog { Gsn = id }, ResGetGuildLog.Parser);
    Check(foreignLog.Result == GetGuildLogResult.NotMember && foreignLog.Logs.Count == 0, "foreign journal leaked");
    var roster = await Read(founder, "guild/chat/get", new ReqGetGuildChatList(), ResGetGuildChatList.Parser);
    Check(roster.MemberList.Single().Usn == (long)founder && roster.ChatList.Count == 0, "chat roster");
    var pending = await Read(founder, "guild/joinrequestlist", new ReqGuildJoinRequestList { Gsn = id }, ResGuildJoinRequestList.Parser);
    Check(pending.Result == GuildJoinRequestListResult.Ok && pending.List.Count == 0, "founder cannot read applications");
    var denied = await Read(other, "guild/joinrequestlist", new ReqGuildJoinRequestList { Gsn = id }, ResGuildJoinRequestList.Parser);
    Check(denied.Result == GuildJoinRequestListResult.NotMember, "outsider read applications");
});
if (!restarting)
{
    await Test("saving union settings updates every editable field without changing the roster or wallet", async () => {
        var created = await Create(940, "設定測試");
        long id = created.Guild.Gsn;
        var joined = await Read(941, "guild/join", new ReqJoinGuild { Gsn = id }, ResJoinGuild.Parser);
        Check(joined.Result == JoinGuildResult.Ok, "settings member fixture could not join");
        var result = await Read(940, "guild/updatesettings2", Settings(id), ResUpdateGuildSettings2.Parser);
        Check(result.Result == SetGuildDescResult.Okay && result.BanEndTime != null, "settings save was not acknowledged");
        var reopened = await Read(940, "guild/get", new ReqGetGuild { Gsn = id }, ResGetGuild.Parser);
        CheckSettings(reopened.Guild);
        Check(reopened.Members.Select(m => m.Usn).Order().SequenceEqual(new long[] { 940, 941 }) &&
              reopened.Members.Single(m => m.Usn == 940).Type == 1 &&
              Saved(940).Currency[CurrencyType.FreeCash] == 400, "settings save changed membership, leader or currency");
        CheckSettings(SavedSettings(id));
    });
    await Test("settings save rejects anonymous users, ordinary members and another union's leader", async () => {
        long id = Saved(940).Guild.guildId!.Value;
        var original = SavedSettings(id).Clone();
        var change = Settings(id);
        change.Desc = "不應儲存";
        using var anonymous = await Send(0, "guild/updatesettings2", change);
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "anonymous settings change accepted");
        var member = await Read(941, "guild/updatesettings2", change, ResUpdateGuildSettings2.Parser);
        Check(member.Result == SetGuildDescResult.Forbidden, "ordinary member changed settings");
        var outsider = await Read(founder, "guild/updatesettings2", change, ResUpdateGuildSettings2.Parser);
        Check(outsider.Result == SetGuildDescResult.NotMember, "foreign leader changed settings");
        Check(original.Equals(SavedSettings(id)), "rejected settings request changed the save");
    });
    await Test("locked emblems and invalid join requirements leave all settings unchanged", async () => {
        long id = Saved(940).Guild.guildId!.Value;
        var original = SavedSettings(id).Clone();
        foreach (var (emblem, joinLevel, joinType) in new[] { (3, 15, 1), (999999, 15, 1), (2001, 9, 1), (2001, 101, 1), (2001, 15, -1), (2001, 15, 3) })
        {
            var change = Settings(id);
            change.Emblem = emblem; change.JoinLevel = joinLevel; change.JoinType = joinType;
            var result = await Read(940, "guild/updatesettings2", change, ResUpdateGuildSettings2.Parser);
            Check(result.Result != SetGuildDescResult.Okay, "invalid requirements were accepted");
            Check(original.Equals(SavedSettings(id)), "invalid request partly changed settings");
        }
    });
    await Test("a failed settings write rolls back memory and disk before retry", async () => {
        long id = Saved(940).Guild.guildId!.Value;
        var original = SavedSettings(id).Clone();
        var change = Settings(id);
        change.Desc = "寫盤失敗";
        var temp = Directory.CreateDirectory(save + ".tmp");
        try
        {
            using var failed = await Send(940, "guild/updatesettings2", change);
            Check(!failed.IsSuccessStatusCode, "failed settings write was acknowledged");
        }
        finally { temp.Delete(); }
        var reopened = await Read(940, "guild/get", new ReqGetGuild { Gsn = id }, ResGetGuild.Parser);
        Check(original.Equals(reopened.Guild) && original.Equals(SavedSettings(id)), "failed write leaked unsaved settings");
        var retry = await Read(940, "guild/updatesettings2", Settings(id), ResUpdateGuildSettings2.Parser);
        Check(retry.Result == SetGuildDescResult.Okay, "settings retry did not recover");
    });
    await Test("open admission and an empty description can be saved after approval-only settings", async () => {
        long id = Saved(940).Guild.guildId!.Value;
        await Read(940, "guild/updatesettings2", Settings(id), ResUpdateGuildSettings2.Parser);
        CheckSettings(SavedSettings(id));
        var change = Settings(id);
        change.Desc = ""; change.JoinType = 0; change.JoinLevel = 10;
        var result = await Read(940, "guild/updatesettings2", change, ResUpdateGuildSettings2.Parser);
        var reopened = await Read(940, "guild/get", new ReqGetGuild { Gsn = id }, ResGetGuild.Parser);
        Check(result.Result == SetGuildDescResult.Okay && reopened.Guild.Desc == "" &&
              reopened.Guild.JoinType == 0 && reopened.Guild.JoinLevel == 10, "protobuf default fields were not saved");
        await Read(940, "guild/updatesettings2", Settings(id), ResUpdateGuildSettings2.Parser);
    });
}
await Test("saved union settings remain visible to members and public profiles after reopening or restart", async () => {
    long id = Saved(940).Guild.guildId!.Value;
    var own = await Read(941, "guild/get", new ReqGetGuild { Gsn = id }, ResGetGuild.Parser);
    var visible = await Read(other, "guild/publicinfo", new ReqGetGuildPublicInfo { Gsn = id }, ResGetGuildPublicInfo.Parser);
    CheckSettings(own.Guild);
    CheckSettings(visible.Guild);
    CheckSettings(SavedSettings(id));
    Check(own.Members.Count == 2 && own.Members.Single(m => m.Usn == 940).Type == 1, "saved settings lost the leader or roster");
});

if (!restarting)
{
    await Test("successful admission records the member and actual membership timestamp once", async () => {
        long id = (await Create(950, "記錄測試")).Guild.Gsn;
        var joined = await Read(951, "guild/join", new ReqJoinGuild { Gsn = id }, ResJoinGuild.Parser);
        Check(joined.Result == JoinGuildResult.Ok, "journal member could not join");
        var log = await Journal(id);
        var entry = log.Logs.SingleOrDefault(e => e.Arguments.Any(a => a.Value == "P951"));
        Check(entry != null && entry.Id.EndsWith("_JOIN") && entry.CreatedAt == Saved(951).Guild.JoinedAt,
            "successful admission has no correctly dated join record");
        await Read(951, "guild/join", new ReqJoinGuild { Gsn = id }, ResJoinGuild.Parser);
        Check((await Journal(id)).Equals(log), "duplicate admission request created another journal entry");
    });
    await Test("leaving retains both membership events and the original creation log", async () => {
        long id = Saved(950).Guild.guildId!.Value;
        await Read(951, "guild/leave", new ReqLeaveGuild { Gsn = id }, ResLeaveGuild.Parser);
        var log = await Journal(id);
        var entries = log.Logs.Where(e => e.Arguments.Any(a => a.Value == "P951")).ToArray();
        Check(entries.Length == 2 && entries.Any(e => e.Id == "GUILD_JOURNAL_JOIN") && entries.Any(e => e.Id == "GUILD_JOURNAL_QUIT"),
            "leaving erased the join event or omitted the leave event");
        Check(log.Logs.Count(e => e.Id == "GUILD_JOURNAL_GUILD_CREATE") == 1 &&
            log.Logs.Select(e => e.Seq).Distinct().Count() == log.Logs.Count, "creation log or sequence identity changed");
        var denied = await Read(951, "guild/getlog", new ReqGetGuildLog { Gsn = id }, ResGetGuildLog.Parser);
        Check(denied.Result == GetGuildLogResult.NotMember && denied.Logs.Count == 0, "departed member can still read the journal");
    });
    await Test("legacy member history is backfilled from saved dates and names without duplicates", async () => {
        var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
        var core = (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
        long id = Saved(950).Guild.guildId!.Value;
        long created = core.Guilds[(int)id].CreationLog.CreatedAt;
        long joinedAt = created + (DateTime.UtcNow.Ticks - created) / 2;
        core.Users.Single(u => u.ID == 953).Guild = new GuildData { guildId = (int)id, MemberType = 3, JoinedAt = joinedAt };
        jsonDb.GetMethod("Save")!.Invoke(null, null);
        var log = await Journal(id);
        var entry = log.Logs.SingleOrDefault(e => e.Arguments.Any(a => a.Value == "P953"));
        Check(entry != null && entry.CreatedAt == joinedAt && entry.Id.EndsWith("_JOIN"), "legacy membership has no correctly dated record");
        Check((await Journal(id)).Equals(log), "reading the journal duplicated legacy history");
        var disk = JObject.Parse(File.ReadAllText(save))["Guilds"]![id.ToString()]!;
        Check(disk["Journal"] is JArray records && records.Any(e => (ulong?)e["MemberId"] == 953), "backfilled record was not persisted");
        Check(log.Logs.Select(e => e.CreatedAt).SequenceEqual(log.Logs.Select(e => e.CreatedAt).OrderDescending()), "journal is not newest first");
    });
    await Test("a failed admission save rolls back the membership and journal together", async () => {
        long id = Saved(950).Guild.guildId!.Value;
        var before = await Journal(id);
        var temp = Directory.CreateDirectory(save + ".tmp");
        try
        {
            using var failed = await Send(952, "guild/join", new ReqJoinGuild { Gsn = id });
            Check(!failed.IsSuccessStatusCode, "failed admission was acknowledged");
        }
        finally { temp.Delete(); }
        Check(Saved(952).Guild.guildId == null && (await Journal(id)).Equals(before), "failed admission leaked membership or a journal entry");
    });
    await Test("managed NPC administrator moves use the supplied simulation clock for both membership events", async () => {
        var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
        var core = (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
        var npc = core.Users.Single(u => u.ID == 954);
        npc.ManagedNpc = new ManagedNpcState { Name = "P954", Seed = 123, CreatedUtc = DateTime.UtcNow };
        long id = Saved(950).Guild.guildId!.Value;
        var simulated = DateTime.UtcNow.AddDays(1);
        ManagedNpcService.Update(954, true, 1, (int)id, simulated);
        ManagedNpcService.Update(954, true, 1, null, simulated.AddHours(1));
        var entries = (await Journal(id)).Logs.Where(e => e.Arguments.Any(a => a.Value == "P954")).ToArray();
        Check(entries.Length == 2 && entries.Any(e => e.Id == "GUILD_JOURNAL_JOIN") && entries.Any(e => e.Id == "GUILD_JOURNAL_QUIT"),
            "managed NPC moves are absent from the journal");
    });
}
await Test("membership journal survives a fresh process with unchanged identities and timestamps", async () => {
    long id = Saved(950).Guild.guildId!.Value;
    var first = await Journal(id);
    var second = await Journal(id);
    Check(first.Equals(second) && first.Logs.Count == 6 && first.Logs.Count(e => e.Arguments.Any(a => a.Value == "P951")) == 2 &&
          first.Logs.Count(e => e.Arguments.Any(a => a.Value == "P953")) == 1 &&
          first.Logs.Count(e => e.Arguments.Any(a => a.Value == "P954")) == 2, "saved journal was lost, duplicated or re-dated");
});

await app.StopAsync();
return failures.Count == 0 ? 0 : 1;

static User Account(ulong id, int level, int free, int paid = 0)
{
    var user = new User { ID = id };
    user.userPointData.UserLevel = level;
    user.Currency[CurrencyType.FreeCash] = free;
    if (paid > 0) user.Currency[CurrencyType.ChargeCash] = paid;
    return user;
}
User Saved(ulong id) => JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))!.Users.Single(u => u.ID == id);
NetGuildData SavedSettings(long id) => JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(save))!.Guilds[(int)id].Data;
static ReqUpdateGuildSettings2 Settings(long id) => new()
{
    Gsn = id, Desc = "歡迎加入\n一起作戰！", Emblem = 2001, Locale = "zh-CN", JoinType = 1, JoinLevel = 15
};
static void CheckSettings(NetGuildData guild) => Check(guild is { Name: "設定測試", Desc: "歡迎加入\n一起作戰！",
    Emblem: 2001, Locale: "zh-CN", JoinType: 1, JoinLevel: 15, Grade: 1 }, "saved settings were lost or incomplete");
static ReqCreateGuild Request(string guildName) => new() { Name = guildName, Locale = "zh-TW", Emblem = 1 };
Task<ResCreateGuild> Create(ulong id, string guildName) => Read(id, "guild/create", Request(guildName), ResCreateGuild.Parser);
Task<ResGetGuildLog> Journal(long id) => Read(950, "guild/getlog", new ReqGetGuildLog { Gsn = id }, ResGetGuildLog.Parser);
async Task<HttpResponseMessage> Send(ulong id, string path, IMessage req, string? nonce = null)
{
    using var message = new HttpRequestMessage(HttpMethod.Post, path);
    if (id != 0) message.Headers.Add("Test-User", id.ToString());
    if (nonce != null) message.Headers.Add("Test-Nonce", nonce);
    message.Content = new ByteArrayContent(req.ToByteArray());
    message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");
    return await client.SendAsync(message);
}
async Task<T> Read<T>(ulong id, string path, IMessage req, MessageParser<T> parser) where T : IMessage<T>
{
    using var r = await Send(id, path, req);
    r.EnsureSuccessStatusCode();
    return parser.ParseFrom(await r.Content.ReadAsByteArrayAsync());
}
async Task Test(string label, Func<Task> action)
{
    try { await action(); Console.WriteLine($"PASS {label}"); }
    catch (Exception ex) { failures.Add(label); Console.WriteLine($"FAIL {label}: {ex.Message}"); }
}
static void Check(bool condition, string reason)
{
    if (!condition) throw new Exception(reason);
}
