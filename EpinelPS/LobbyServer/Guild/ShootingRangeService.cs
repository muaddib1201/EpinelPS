using System.Security.Cryptography;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Utils;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace EpinelPS.LobbyServer.Guild;

internal static class ShootingRangeService
{
    private const int HistoryLimit = 100;

    // All callers hold JsonDb.SaveLock, matching guild membership reads and atomic saves.
    internal static ResGetShootingRangeRankingV2 Rankings(User user, SavedGuild guild)
    {
        var response = new ResGetShootingRangeRankingV2();
        var profiles = JsonDb.Instance.Users.Where(u => u.Guild.guildId == guild.Data.Gsn)
            .ToDictionary(u => u.ID, Profile);
        // The client indexes by element/length, so fixed-spec rows would duplicate keys.
        foreach (var stage in GameData.Instance.ShootingRanges.Values.Where(s => !s.SpecActiveSpot).OrderBy(s => s.Id))
        {
            var board = new NetShootingRangeRankingTotalData { ShootingRangeId = stage.Id };
            long rank = 0;
            foreach (var (member, score) in RankedMembers(guild, stage.Id))
                board.GuildRankingList.Add(new NetShootingRangeRankingData { Rank = ++rank, Score = score, User = profiles[member.ID].Clone() });
            board.UserGuildRanking = board.GuildRankingList.FirstOrDefault(r => r.User.Usn == (long)user.ID)?.Clone()
                ?? new NetShootingRangeRankingData { User = profiles[user.ID].Clone() };
            response.RankingList.Add(board);
        }
        return response;
    }

    internal static ResGetShootingRangeBattleLog History(User user) => new()
    {
        BattleLogList = { user.ShootingRangeBattles.Select(b => NetShootingRangeBattleLog.Parser.ParseFrom(b.BattleLog)) }
    };

    internal static ResSetShootingRangeScoreV2 Submit(User user, ReqSetShootingRangeScoreV2 req, byte[]? nonce)
    {
        string? key = nonce is { Length: > 0 } ? Convert.ToHexString(SHA256.HashData([.. nonce, .. req.ToByteArray()])) : null;
        // A completed packet belongs to its original battle, even after leaving/changing guilds.
        if (key != null && user.ShootingRangeBattles.FirstOrDefault(b => b.RequestKey == key) is { } previous)
            return ResSetShootingRangeScoreV2.Parser.ParseFrom(previous.Receipt);
        if (GuildDirectory.Find(user.Guild.guildId ?? 0) is not { } guild)
            return new ResSetShootingRangeScoreV2 { Result = ShootingRangeRankingResult.GetShootingRangeRankingResultNotMember };
        if (!GameData.Instance.ShootingRanges.TryGetValue(req.ShootingRangeId, out var stage))
            throw new BadHttpRequestException("Unknown shooting range stage.", 400);
        if (req.Score < 0) throw new BadHttpRequestException("A shooting range score cannot be negative.", 400);

        var regular = GameData.Instance.ShootingRanges.Values.Single(s => !s.SpecActiveSpot &&
            s.ShootingRangeType == stage.ShootingRangeType && s.ShootingRangeBattleLengthType == stage.ShootingRangeBattleLengthType);
        var oldScores = guild.ShootingRangeScores;
        var scores = oldScores.GetValueOrDefault(regular.Id) ?? [];
        long best = scores.GetValueOrDefault(user.ID);
        var response = new ResSetShootingRangeScoreV2 { PrevRanking = Rank(user, guild, regular.Id), HighestScore = best };
        response.IsNewRecord = !stage.SpecActiveSpot && req.Score > best;
        var nextScores = new Dictionary<int, Dictionary<ulong, long>>(oldScores);
        if (response.IsNewRecord)
        {
            nextScores[regular.Id] = new Dictionary<ulong, long>(scores) { [user.ID] = req.Score };
            response.HighestScore = req.Score;
        }

        var log = Snapshot(user, stage, req);
        var oldBattles = user.ShootingRangeBattles;
        // Update both in memory under the save lock, then roll back both if disk commit fails.
        guild.ShootingRangeScores = nextScores;
        response.NextRanking = Rank(user, guild, regular.Id);
        user.ShootingRangeBattles = [new SavedShootingRangeBattle
        {
            GuildId = guild.Data.Gsn, BattleLog = log.ToByteArray(), RequestKey = key, Receipt = response.ToByteArray()
        }, .. oldBattles.Take(HistoryLimit - 1)];
        try { JsonDb.Save(); }
        catch
        {
            guild.ShootingRangeScores = oldScores;
            user.ShootingRangeBattles = oldBattles;
            throw;
        }
        return response;
    }

    private static List<(User Member, long Score)> RankedMembers(SavedGuild guild, int stage) => JsonDb.Instance.Users
        .Where(u => u.Guild.guildId == guild.Data.Gsn)
        .Select(u => (Member: u, Score: guild.ShootingRangeScores.GetValueOrDefault(stage)?.GetValueOrDefault(u.ID) ?? 0))
        .Where(r => r.Score > 0).OrderByDescending(r => r.Score).ThenBy(r => r.Member.ID).ToList();

    private static long Rank(User user, SavedGuild guild, int stage)
    {
        int index = RankedMembers(guild, stage).FindIndex(r => r.Member.ID == user.ID);
        return index < 0 ? 0 : index + 1;
    }

    private static NetWholeUserData Profile(User user)
    {
        var profile = LobbyHandler.CreateWholeUserDataFromDbUser(user);
        profile.UserTitleDisplayData = new();
        return profile;
    }

    private static NetShootingRangeBattleLog Snapshot(User user, ShootingRangeRecord stage, ReqSetShootingRangeScoreV2 req)
    {
        var preset = SelectedTeam(user, stage);
        var log = new NetShootingRangeBattleLog
        {
            ShootingRangeId = stage.Id, Score = req.Score, BattleResult = req.BattleResult,
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow), UserTeam = new NetTeamData { TeamNumber = preset.TeamNumber }
        };
        var reported = req.AntiCheatBattleData?.Characters.Where(c => c.Csn > 0 || c.SupportCharacterId > 0).Take(5).ToList() ?? [];
        if (reported.Count > 0)
        {
            bool zeroBased = reported.Any(c => c.Slot == 0);
            foreach (var entry in reported)
            {
                int slot = entry.Slot + (zeroBased ? 1 : 0);
                if (slot is < 1 or > 5 || log.UserTeam.Slots.Any(s => s.Slot == slot))
                    slot = Enumerable.Range(1, 5).First(s => log.UserTeam.Slots.All(t => t.Slot != s));
                bool support = entry.SupportCharacterId > 0;
                log.UserTeam.Slots.Add(new NetTeamSlot { Slot = slot, Value = support ? entry.SupportCharacterId : entry.Csn, ValueType = support ? 1 : 0 });
                var owned = user.GetCharacterBySerialNumber(entry.Csn);
                log.CharacterDataList.Add(Character(user, owned, owned?.Tid ?? entry.Tid, slot, entry.CharacterSpec));
            }
        }
        else
        {
            foreach (var entry in preset.Slots.Where(s => s.Value > 0).Take(5))
            {
                var owned = entry.ValueType == 0 ? user.GetCharacterBySerialNumber(entry.Value) : null;
                if (owned == null) continue;
                log.UserTeam.Slots.Add(entry.Clone());
                log.CharacterDataList.Add(Character(user, owned, owned.Tid, entry.Slot, null));
            }
        }
        return log;
    }

    private static NetTeamData SelectedTeam(User user, ShootingRangeRecord stage)
    {
        int elementType = (int)TeamType.ShootingRangeFire + (int)stage.ShootingRangeType;
        if (!user.UserTeams.TryGetValue(elementType, out var teams)) user.UserTeams.TryGetValue((int)TeamType.ShootingRange, out teams);
        return teams?.Teams.FirstOrDefault(t => t.TeamNumber == teams.LastContentsTeamNumber)
            ?? teams?.Teams.FirstOrDefault() ?? new NetTeamData { TeamNumber = 1 };
    }

    private static NetShootingRangeBattleLogCharacterData Character(User user, CharacterModel? owned, int tid, int slot, NetAntiCheatCharacterSpec? spec)
    {
        var table = GameData.Instance.CharacterTable.GetValueOrDefault(tid);
        int grade = owned?.Grade ?? 0;
        return new NetShootingRangeBattleLogCharacterData
        {
            Tid = tid, NameCode = table?.NameCode ?? 0, Slot = slot, CostumeId = owned?.CostumeId ?? 0,
            Lv = spec?.Level > 0 ? spec.Level : owned == null ? 1 : user.GetCharacterLevel(owned.Csn, owned.Level),
            Grade = spec?.Grade ?? Math.Min(3, grade), Core = spec?.Core ?? Math.Max(0, grade - 3),
            Combat = spec == null ? owned == null ? 0 : FormulaUtils.CalculateCP(user, owned.Csn)
                : (int)Math.Clamp(spec.Combat, 0, int.MaxValue)
        };
    }
}
