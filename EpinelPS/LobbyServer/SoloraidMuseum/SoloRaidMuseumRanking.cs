using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Soloraid;

namespace EpinelPS.LobbyServer.SoloraidMuseum;

internal static class SoloRaidMuseumRanking
{
    public static NetSoloRaidMuseumStagePastRanking GetBoard(User viewer, MuseumStageRecord_Raw stage,
        IReadOnlyDictionary<ulong, string> names)
    {
        lock (JsonDb.SaveLock)
        {
            var ranked = JsonDb.Instance.Users.Where(u => !u.IsBanned && names.ContainsKey(u.ID))
                .Select(u => (User: u, Damage: u.SoloRaidMuseumData.GetValueOrDefault(stage.Id)?.Challenge.BestDamage ?? 0))
                .Where(r => r.Damage > 0)
                .OrderByDescending(r => r.Damage).ThenBy(r => r.User.ID).ToList();
            long normalHp = GameData.Instance.MuseumCommonStageHpGroupTable.Values
                .Where(h => h.Group == stage.SoloRaidStageHpGroup).Sum(h => h.HpValue);
            int ownIndex = ranked.FindIndex(r => r.User.ID == viewer.ID);
            var board = new NetSoloRaidMuseumStagePastRanking
            {
                StageId = stage.Id,
                TotalUserCount = ranked.Count,
                UserRanking = new NetSoloRaidMuseumUserRankingData
                {
                    Ranking = ownIndex + 1,
                    Damage = ownIndex < 0 ? 0 : SoloRaidHelper.SeasonTotal(ranked[ownIndex].Damage, normalHp),
                    CurrentUserData = SoloRaidHelper.RankingProfile(viewer, names.GetValueOrDefault(viewer.ID) ?? ""),
                },
            };

            // The mission page looks up these exact rank numbers, even on an empty server.
            // Keep vacant slots addressable without assigning a competitor or a score.
            var ranks = GameData.Instance.MuseumMissionTable.Values.Where(m => m.StageId == stage.Id &&
                m.ModeType == MuseumStageModeType.Challenge && m.ConditionType == MuseumMissionConditionType.GetTotalDamageRanker)
                .Select(m => m.ConditionValue).Distinct().OrderDescending();
            foreach (long rank in ranks)
            {
                var slot = new NetSoloRaidMuseumRankerRankingData { Ranking = rank };
                if (rank > 0 && rank <= ranked.Count)
                {
                    var entry = ranked[(int)rank - 1];
                    var profile = SoloRaidHelper.RankingProfile(entry.User, names[entry.User.ID]);
                    slot.CurrentUserData = profile;
                    slot.PastUserData = new NetPastWholeUserData
                    {
                        Usn = profile.Usn, Nickname = profile.Nickname, Icon = profile.Icon, IconPrism = profile.IconPrism,
                        Frame = profile.Frame, GuildName = profile.GuildName, UserTitleId = profile.UserTitleId,
                        UserTitleDisplayData = profile.UserTitleDisplayData.Clone(),
                    };
                    slot.Damage = SoloRaidHelper.SeasonTotal(entry.Damage, normalHp);
                }
                board.RankerRankingList.Add(slot);
            }
            return board;
        }
    }

    public static NetSoloRaidMuseumLog[] GetSquads(int stageId, long targetUsn)
    {
        lock (JsonDb.SaveLock)
        {
            if (targetUsn <= 0 || !GameData.Instance.MuseumStageTable.ContainsKey(stageId) ||
                JsonDb.GetUser((ulong)targetUsn) is not { IsBanned: false } target ||
                target.SoloRaidMuseumData.GetValueOrDefault(stageId)?.Challenge.BestDamage is not > 0)
                return [];

            using var db = GameContext.CreateNew();
            if (!db.Users.Any(u => u.ID == target.ID)) return [];
            return SoloRaidMuseumHelper.GetLogs(target, stageId, noLimit: false).ToArray();
        }
    }
}
