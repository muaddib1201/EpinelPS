using System.Security.Cryptography;
using System.Text;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;
using Google.Protobuf;
using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.Arena;

// The controller owns user -> SaveLock. Never lock a second user while holding SaveLock.
internal sealed partial class ArenaService(User user, DateTime now, byte[]? nonce)
{
    private static readonly DateTime Anchor = new(2026, 11, 5, 20, 0, 0, DateTimeKind.Utc);
    private ArenaPlayer Own => user.Arena!;
    private ArenaWorld World => JsonDb.Instance.Arena!;
    private readonly Dictionary<string, string> requestHashes = [];
    private static ArenaRecord? SpecialConfig => GameData.Instance.Arenas.Values.FirstOrDefault(r => r.ArenaType == ArenaType.ArenaSpecial);
    private static int Config(string key, int fallback) => int.TryParse(GameData.Instance.GameSettings.GetValueOrDefault(key)?.Value, out int n) ? n : fallback;
    private static int SeasonAt(DateTime at) => Math.Max(0, (int)Math.Floor((at - Anchor).TotalDays / 14));
    private static DateTime SeasonStart(int season) => Anchor.AddDays(14d * season);
    private static int DayAt(DateTime at) => int.Parse(at.AddHours(-JsonDb.Instance.ResetHourUtcTime).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
    private static DateTime NextDay(DateTime at)
    {
        var boundary = at.Date.AddHours(JsonDb.Instance.ResetHourUtcTime);
        return boundary > at ? boundary : boundary.AddDays(1);
    }

    internal T Execute<T>(Func<T> action)
    {
        var core = JsonDb.Instance;
        string before = JsonConvert.SerializeObject(core);
        var backup = JsonConvert.DeserializeObject<CoreInfo>(before)!;
        try
        {
            now = new[] { DateTime.SpecifyKind(now, DateTimeKind.Utc), core.Arena?.LastUtc ?? DateTime.MinValue }.Max();
            Advance();
            T response = action();
            if (before != JsonConvert.SerializeObject(core)) JsonDb.Save();
            return response;
        }
        catch
        {
            core.Arena = backup.Arena;
            // Preserve identities used by waiting requests; restore exact field values, not merge-populate collections.
            foreach (var saved in backup.Users)
            {
                var target = core.Users.Single(u => u.ID == saved.ID);
                foreach (var p in typeof(User).GetProperties().Where(p => p.CanRead && p.CanWrite)) p.SetValue(target, p.GetValue(saved));
                foreach (var f in typeof(User).GetFields()) f.SetValue(target, f.GetValue(saved));
            }
            throw;
        }
    }

    private void Advance()
    {
        JsonDb.Instance.Arena ??= new ArenaWorld { LastUtc = now, NextDayUtc = NextDay(now), Season = SeasonAt(now) };
        Enroll();
        while (true)
        {
            var at = new[] { World.NextDayUtc, SeasonStart(World.Season + 1) }.Min();
            if (at > now) break;
            AccrueAll(at);
            if (at == World.NextDayUtc) { DailyRewards(at); World.NextDayUtc = World.NextDayUtc.AddDays(1); }
            if (at == SeasonStart(World.Season + 1)) { SeasonRewards(at); World.Season++; }
        }
        AccrueAll(now);
        foreach (var u in JsonDb.Instance.Users.Where(u => u.Arena != null))
        {
            var a = u.Arena!;
            if (a.Day < DayAt(now)) { a.Day = DayAt(now); a.RookieCount = 0; a.SpecialCount = 0; }
        }
        World.LastUtc = now;
    }

    private void Enroll()
    {
        foreach (var u in JsonDb.Instance.Users)
        {
            if (!ArenaTeams.Unlocked(u, false) || !ArenaTeams.Ensure(u, false, false)) continue;
            u.Arena ??= new ArenaPlayer { Day = DayAt(now) };
            u.Arena.RookieJoinedUtc ??= now;
            ArenaTeams.Ensure(u, false, true);
        }
        var newcomers = JsonDb.Instance.Users.Where(u => ArenaTeams.Unlocked(u, true) && ArenaTeams.Ensure(u, true, false) && u.Arena?.SpecialJoinedUtc == null)
            .OrderByDescending(u => Combat(u, true)).ThenBy(u => u.ID).ToArray();
        long rank = JsonDb.Instance.Users.Select(u => u.Arena?.SpecialRank ?? 0).DefaultIfEmpty().Max();
        foreach (var u in newcomers)
        {
            u.Arena ??= new ArenaPlayer { Day = DayAt(now) };
            u.Arena.SpecialJoinedUtc = now; u.Arena.SpecialRank = ++rank; u.Arena.AccruedUtc = now;
            ArenaTeams.Ensure(u, true, true);
        }
    }

    private static List<User> Participants(bool special) => JsonDb.Instance.Users.Where(u => !u.IsBanned &&
        (special ? u.Arena?.SpecialJoinedUtc != null : u.Arena?.RookieJoinedUtc != null) &&
        ArenaTeams.Unlocked(u, special) && ArenaTeams.Ensure(u, special, false))
        .OrderBy(u => special ? u.Arena!.SpecialRank : -(long)u.Arena!.Rating).ThenBy(u => u.ID).ToList();
    private static long Rank(User u, bool special) => special ? u.Arena?.SpecialRank ?? 0 : Participants(false).FindIndex(v => v.ID == u.ID) + 1;
    private static int Tier(User u, bool special)
    {
        long rank = Rank(u, special);
        var rows = GameData.Instance.ArenaTiers.Values.Where(t => t.ArenaType == (special ? ArenaType.ArenaSpecial : ArenaType.ArenaNormal)).OrderBy(t => t.Priority).ToArray();
        return rows.FirstOrDefault(t => t.GrantType == GrantType.Rank && rank >= t.GrantValueMin && (t.GrantValueMax == 0 || rank <= t.GrantValueMax))?.Id
            ?? rows.FirstOrDefault(t => t.GrantType == GrantType.None)?.Id ?? 0;
    }
    private static int Combat(User u, bool special) => u.UserTeams.GetValueOrDefault(ArenaTeams.Type(special, false))?.Teams
        .SelectMany(t => t.Slots).Sum(s => FormulaUtils.CalculateCP(u, s.Value)) ?? 0;
    private static NetWholeUserData Profile(User u, bool special)
    {
        var p = LobbyHandler.CreateWholeUserDataFromDbUser(u);
        p.IsBot = false; p.TeamCombat = Combat(u, special); p.UserTitleDisplayData = new();
        return p;
    }
    private static NetArenaData Data(User u, bool special) => new() { Rank = Rank(u, special), Rating = special ? 0 : u.Arena!.Rating, Tier = Tier(u, special), User = Profile(u, special) };
    private NetSchedule Schedule() => new() { OnlyOnce = new NetOnlyOnceSchedule { StartAt = SeasonStart(World.Season).Ticks, EndAt = SeasonStart(World.Season + 1).Ticks } };
    private void Require(bool special)
    {
        if (!ArenaTeams.Unlocked(user, special)) throw new BadHttpRequestException("Arena content is not unlocked.", 403);
        if (user.Arena == null || (special ? Own.SpecialJoinedUtc == null : Own.RookieJoinedUtc == null))
            throw new BadHttpRequestException(special ? "Special arena requires fifteen distinct owned characters." : "Arena requires a valid five-character team.", 400);
    }
    private User Enemy(long id, bool special)
    {
        Require(special);
        if (id <= 0 || (ulong)id == user.ID) throw new BadHttpRequestException("Invalid arena opponent.", 400);
        return Participants(special).FirstOrDefault(u => u.ID == (ulong)id) ?? throw new BadHttpRequestException("Arena opponent is unavailable.", 404);
    }
    private IEnumerable<User> Rivals(bool special)
    {
        Require(special);
        return special ? SpecialRivals() : RookieRivals();
    }

    internal ResGetArena Get()
    {
        Require(false);
        return new ResGetArena { Server = 1001, User = Data(user, false), ArenaCount = Own.RookieCount,
            DefenseTeam = user.UserTeams[3].Clone(), Schedules = { Schedule() } };
    }
    internal ResGetSpecialArena GetSpecial()
    {
        Require(true);
        return new ResGetSpecialArena { Server = 1001, User = Data(user, true), SpecialArenaCount = Own.SpecialCount,
            SpecialArenaPoint = Points(user), Schedules = { Schedule() } };
    }
    internal ResGetArenaEnemyList Enemies() => new() { List2 = { Rivals(false).Select(u => new NetArenaRivalData {
        User = Data(u, false), Team = ArenaTeams.Whole(u, u.UserTeams[3].Teams[0], 3) }) } };
    internal ResGetSpecialArenaEnemyList SpecialEnemies() => new() { List2 = { Rivals(true).Select(u => new NetSpecialArenaRivalData {
        User = Data(u, true), TotalCombat = Combat(u, true), RoundCharacter = { u.UserTeams[12].Teams.OrderBy(t => t.TeamNumber).SelectMany(t =>
            t.Slots.Select(s => { var c = u.GetCharacterBySerialNumber(s.Value)!; return new NetSpecialArenaCharacter { Round = t.TeamNumber, Csn = c.Csn, Tid = c.Tid,
                Lv = u.GetCharacterLevel(c.Csn, c.Level), Combat = FormulaUtils.CalculateCP(u, c.Csn), CostumeId = c.CostumeId }; })) } }) } };
    internal ResGetArenaEnemyData EnemyData(long id) { var u = Enemy(id, false); return new() { Team = ArenaTeams.Whole(u, u.UserTeams[3].Teams[0], 3) }; }
    internal ResGetSpecialArenaEnemyData SpecialEnemyData(long id)
    {
        var u = Enemy(id, true);
        return new() { Teams = { u.UserTeams[12].Teams.OrderBy(t => t.TeamNumber).Select(t => new NetSpecialArenaRoundTeam { Round = t.TeamNumber, TeamNumber = t.TeamNumber, Team = ArenaTeams.Whole(u, t, 12) }) } };
    }
    internal ResGetArenaTopRank Top() { Require(false); return new() { User = Data(user, false), RankList = { Participants(false).Take(100).Select(u => Data(u, false)) } }; }
    internal ResGetSpecialArenaTopRank SpecialTop() { Require(true); return new() { RankList = { Participants(true).Take(100).Select(u => Data(u, true)) } }; }
    internal ResGetSpecialArenaProfile SpecialProfile(long id)
    {
        var u = id == (long)user.ID ? user : Enemy(id, true); Require(true);
        ArenaTeams.Ensure(u, true, true);
        return new() { Defense = { u.UserTeams[12].Teams.Select(t => ArenaTeams.Whole(u, t, 12)) }, Attack = { u.UserTeams[13].Teams.Select(t => ArenaTeams.Whole(u, t, 13)) } };
    }
    internal ResGetArenaRecordList Records() { Require(false); return new() { List = { Own.RookieRecords.Select(NetRookieArenaRecordData.Parser.ParseFrom) } }; }
    internal ResGetSpecialArenaRecordList SpecialRecords() { Require(true); return new() { List = { Own.SpecialRecords.Select(NetSpecialArenaRecordData.Parser.ParseFrom) } }; }

    private string? Key(string route, IMessage req)
    {
        if (nonce is not { Length: > 0 }) return null;
        string key = Convert.ToHexString(SHA256.HashData([.. Encoding.UTF8.GetBytes(route), .. nonce]));
        string body = Convert.ToHexString(SHA256.HashData(req.ToByteArray()));
        if (user.Arena?.PacketHashes.TryGetValue(key, out var previous) == true && previous != body)
            throw new BadHttpRequestException("Arena packet nonce was reused for a different request.", 409);
        requestHashes[key] = body;
        return key;
    }
    private T? Replay<T>(string? key) where T : class, IMessage<T>, new()
    {
        if (key == null) return null;
        if (user.Arena?.Receipts.FirstOrDefault(r => r.Key == key) is { } r)
            return new MessageParser<T>(() => new T()).ParseFrom(r.Response);
        if (user.Arena?.PacketHashes.ContainsKey(key) == true)
            throw new BadHttpRequestException("This arena packet was already processed; its response has expired.", 409);
        return null;
    }
    private T Remember<T>(string? key, T response) where T : IMessage
    {
        if (key != null)
        {
            Own.PacketHashes[key] = requestHashes[key];
            Own.Receipts = [new ArenaReceipt { Key = key, AtUtc = now, Response = response.ToByteArray() },
                .. Own.Receipts.Where(r => r.Key != key).Where((r, i) => i < 127 || r.AtUtc >= now.AddDays(-2))];
        }
        return response;
    }
}
