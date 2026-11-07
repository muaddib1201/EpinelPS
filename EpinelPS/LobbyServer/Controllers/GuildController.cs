using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Interfaces;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.Utils;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.LobbyServer.Controllers;

[ApiController]
[Route("/v1/guild")]
public class GuildController(IUserService users, GameContext db) : ControllerBase
{
    [HttpPost("create")]
    public ActionResult<ResCreateGuild> Create([FromBodyProtobuf] ReqCreateGuild req)
    {
        var user = users.GetUser();
        if (user == null) return Problem(statusCode: 401, type: NetUtils.InvalidSessionErrorType);
        lock (user)
        lock (JsonDb.SaveLock)
        {
            // A concurrent admin reload replaces account instances.
            if (JsonDb.GetUser(user.ID) != user) return Problem(statusCode: 409, type: NetUtils.InvalidSessionErrorType);
            var nonce = HttpContext.Items[nameof(PacketDecryptResponse.Nonce)] as byte[];
            try { return GuildDirectory.Create(db, user, req, nonce); }
            catch (BadHttpRequestException ex) { return Problem(statusCode: ex.StatusCode, detail: ex.Message, type: NetUtils.InvalidSessionErrorType); }
        }
    }

    [HttpPost("get")]
    public ActionResult<ResGetGuild> Get([FromBodyProtobuf] ReqGetGuild req) => Read(user =>
    {
        var result = new ResGetGuild { IsSimple = req.IsSimple };
        if (OwnGuild(user, req.Gsn) is { } guild)
        {
            result.Guild = guild.Data;
            result.Members.AddRange(GuildDirectory.Members(db, guild));
        }
        return result;
    });

    [HttpPost("join")]
    public ActionResult<ResJoinGuild> Join([FromBodyProtobuf] ReqJoinGuild req) => Read(user =>
    {
        if (user.Guild.guildId != null) return new ResJoinGuild { Result = JoinGuildResult.AlreadyJoined };
        var guild = GuildDirectory.Find(req.Gsn);
        if (guild == null) return new ResJoinGuild { Result = JoinGuildResult.Reject };
        int unlockLevel = GameData.Instance.ContentsOpenTable[Data.ContentsOpen.Guild].OpenCondition
            .Single(c => c.OpenConditionType == ContentsOpenCondition.UserLevel).OpenConditionValue;
        if (user.userPointData.UserLevel < Math.Max(unlockLevel, guild.Data.JoinLevel))
            return new ResJoinGuild { Result = JoinGuildResult.InvalidLevel };
        if (guild.Data.JoinType != 0) return new ResJoinGuild { Result = JoinGuildResult.UnableJoin };
        var now = DateTime.UtcNow;
        if (user.Guild.LeaveAt is long left && left > now.AddSeconds(-GuildDirectory.RejoinCooldownSeconds()).Ticks)
            return new ResJoinGuild { Result = JoinGuildResult.UnableJoin };
        if (!GameData.Instance.GuildLevels.TryGetValue(guild.Data.Grade, out var level) ||
            JsonDb.Instance.Users.Count(u => u.Guild.guildId == guild.Data.Gsn) >= level.MaxMemberCount)
            return new ResJoinGuild { Result = JoinGuildResult.Full };
        var previous = user.Guild;
        var previousJournal = guild.Journal;
        user.Guild = new GuildData { guildId = (int)guild.Data.Gsn, MemberType = 3, JoinedAt = now.Ticks, ActivityByDay = [] };
        try
        {
            GuildJournal.Joined(guild, user, GuildJournal.Nickname(db, user), now);
            JsonDb.Save();
        }
        catch { user.Guild = previous; guild.Journal = previousJournal; throw; }
        return new ResJoinGuild();
    });

    [HttpPost("leave")]
    public ActionResult<ResLeaveGuild> Leave([FromBodyProtobuf] ReqLeaveGuild req) => Read(user =>
    {
        var guild = GuildDirectory.Find(user.Guild.guildId ?? 0);
        if (guild == null || (req.Gsn != 0 && req.Gsn != guild.Data.Gsn))
            throw new BadHttpRequestException("Not a member of this Union.", 400);
        if (user.Guild.MemberType == GuildDirectory.Master) return new ResLeaveGuild { Result = LeaveGuildResult.MasterCantLeave };
        var previous = user.Guild;
        var previousJournal = guild.Journal;
        long left = DateTime.UtcNow.Ticks;
        try
        {
            GuildJournal.Left(guild, user, GuildJournal.Nickname(db, user), new DateTime(left, DateTimeKind.Utc));
            user.Guild = new GuildData { LeaveAt = left };
            JsonDb.Save();
        }
        catch { user.Guild = previous; guild.Journal = previousJournal; throw; }
        return new ResLeaveGuild { GuildLeaveAt = left };
    });

    [HttpPost("publicinfo")]
    public ActionResult<ResGetGuildPublicInfo> PublicInfo([FromBodyProtobuf] ReqGetGuildPublicInfo req) => Read(_ =>
    {
        var result = new ResGetGuildPublicInfo();
        if (GuildDirectory.Find(req.Gsn) is { } guild)
        {
            result.Guild = guild.Data;
            result.Members.AddRange(GuildDirectory.Members(db, guild));
        }
        return result;
    });

    [HttpPost("recommendlist")]
    public ActionResult<ResRecommendGuildList> Recommend([FromBodyProtobuf] ReqRecommendGuildList req) => Read(_ =>
    {
        var result = new ResRecommendGuildList();
        result.Guilds.AddRange(JsonDb.Instance.Guilds.Values.Select(GuildDirectory.Summary));
        return result;
    });

    [HttpPost("search")]
    public ActionResult<ResSearchGuildList> Search([FromBodyProtobuf] ReqSearchGuildList req) => Read(_ =>
    {
        var result = new ResSearchGuildList();
        string text = req.Search.Trim().Normalize();
        if (text.Length == 0) return result;
        if (text.All(char.IsDigit))
        {
            if (long.TryParse(text, out long id) && GuildDirectory.Find(id) is { } guild)
                result.Guilds.Add(GuildDirectory.Summary(guild));
            return result;
        }
        var matches = JsonDb.Instance.Guilds.Values.Where(g => g.Data.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
        result.Guilds.AddRange(matches.Select(GuildDirectory.Summary));
        return result;
    });

    [HttpPost("getlog")]
    public ActionResult<ResGetGuildLog> Log([FromBodyProtobuf] ReqGetGuildLog req) => Read(user =>
        OwnGuild(user, req.Gsn) is { } guild
            ? new ResGetGuildLog { Logs = { GuildJournal.Read(db, guild) } }
            : new ResGetGuildLog { Result = GetGuildLogResult.NotMember });

    // A newly created grade-one Union cannot enter a raid. The hall still reads its status.
    [HttpPost("unionraid/get")]
    public ActionResult<ResGetUnionRaidData> RaidStatus([FromBodyProtobuf] ReqGetUnionRaidData req) => Read(user =>
        new ResGetUnionRaidData { Data = new NetUnionRaidData
        {
            UnionRaidState = UnionRaidState.Disable, PeriodData = new(), UnionRaidJoinData = new(),
            // The client switches on this protobuf oneof even when there is no active boss.
            CurrentStatus = new() { Gsn = OwnGuild(user, req.Gsn)?.Data.Gsn ?? 0, LeftHp = 0 },
            UnionRaidRanking = new()
        } });

    [HttpPost("unionraid/getjoindata")]
    public ActionResult<ResGetUnionRaidJoinData> RaidAttempts([FromBodyProtobuf] ReqGetUnionRaidJoinData req) => Read(_ =>
        new ResGetUnionRaidJoinData { UnionRaidJoinData = new() });

    [HttpPost("chat/get")]
    public ActionResult<ResGetGuildChatList> Chat([FromBodyProtobuf] ReqGetGuildChatList req) => Read(user =>
    {
        var guild = OwnGuild(user, 0);
        if (guild == null) return new ResGetGuildChatList { Result = GetGuildChatListResult.NotMember };
        var result = new ResGetGuildChatList();
        result.MemberList.AddRange(GuildDirectory.Members(db, guild));
        return result;
    });

    [HttpPost("joinrequestlist")]
    public ActionResult<ResGuildJoinRequestList> Applications([FromBodyProtobuf] ReqGuildJoinRequestList req) => Read(user =>
        new ResGuildJoinRequestList { Result = OwnGuild(user, req.Gsn) == null ? GuildJoinRequestListResult.NotMember :
            user.Guild.MemberType == GuildDirectory.Master ? GuildJoinRequestListResult.Ok : GuildJoinRequestListResult.Forbidden });

    [HttpPost("updatesettings2")]
    public ActionResult<ResUpdateGuildSettings2> UpdateSettings([FromBodyProtobuf] ReqUpdateGuildSettings2 req) => Read(user =>
    {
        var result = new ResUpdateGuildSettings2 { BanEndTime = new() };
        var guild = OwnGuild(user, req.Gsn);
        if (guild == null)
        {
            result.Result = SetGuildDescResult.NotMember;
            return result;
        }
        if (user.Guild.MemberType != GuildDirectory.Master)
        {
            result.Result = SetGuildDescResult.Forbidden;
            return result;
        }
        var data = GameData.Instance;
        int minLevel = int.Parse(data.GameSettings["DefaultGuildJoinLevel"].Value!);
        int maxLevel = int.Parse(data.GameSettings["MaxGuildJoinLevel"].Value!);
        if (!data.GuildEmblems.TryGetValue(req.Emblem, out var emblem) || emblem.Grade > guild.Data.Grade ||
            req.JoinLevel < minLevel || req.JoinLevel > maxLevel || req.JoinType is < 0 or > 2)
        {
            result.Result = SetGuildDescResult.Inappropriate;
            return result;
        }

        // Keep the old settings intact if the atomic save cannot be committed.
        var previous = guild.Data;
        var updated = previous.Clone();
        updated.Desc = req.Desc;
        updated.Emblem = req.Emblem;
        updated.Locale = req.Locale;
        updated.JoinType = req.JoinType;
        updated.JoinLevel = req.JoinLevel;
        guild.Data = updated;
        try { JsonDb.Save(); }
        catch { guild.Data = previous; throw; }
        return result;
    });

    private static SavedGuild? OwnGuild(User user, long id) =>
        id == 0 || id == user.Guild.guildId ? GuildDirectory.Find(user.Guild.guildId ?? 0) : null;

    private ActionResult<T> Read<T>(Func<User, T> query)
    {
        lock (JsonDb.SaveLock)
        {
            var user = users.GetUser();
            if (user == null) return Problem(statusCode: 401, type: NetUtils.InvalidSessionErrorType);
            return query(user);
        }
    }
}
