using EpinelPS.Data;

namespace EpinelPS.LobbyServer.Team;

public static class SupportCharacterHelper
{
    public static List<NetSupportCharacterUsedCount> GetUsedCounts(User user, IEnumerable<int> teamTypes)
    {
        lock (user)
        {
            user.ResetDataIfNeeded();
            List<NetSupportCharacterUsedCount> result = [];
            foreach (int teamType in teamTypes.Distinct())
            {
                var content = GetContent((TeamType)teamType);
                if (content == null || content.UseValue <= 0 ||
                    !user.ResetableData.SupportCharacterUsedCounts.TryGetValue(teamType, out var counts))
                    continue;

                foreach (var (characterId, count) in counts)
                    result.Add(new NetSupportCharacterUsedCount
                    {
                        TeamType = teamType,
                        SupportCharacterId = characterId,
                        UsedSupportCharacterCount = Math.Clamp(count, 0, content.UseValue)
                    });
            }
            return result;
        }
    }

    public static void BeginBattle(User user, TeamType teamType, int battleId, int teamNumber)
    {
        lock (user)
        {
            user.ResetDataIfNeeded();
            var team = user.UserTeams.GetValueOrDefault((int)teamType)?.Teams
                .FirstOrDefault(t => t.TeamNumber == teamNumber);
            var characterIds = team?.Slots.Where(s => s.ValueType == 1)
                .Select(s => checked((int)s.Value)).Distinct().ToList() ?? [];
            var content = GetContent(teamType);
            if (characterIds.Count > 0)
            {
                if (content == null)
                    throw new InvalidOperationException($"Support characters are unavailable for {teamType}");

                var allowed = GameData.Instance.SupportCharacterGroupTable.Values
                    .Where(g => g.GroupId == content.GroupId).Select(g => g.SupportCharacterId).ToHashSet();
                var counts = user.ResetableData.SupportCharacterUsedCounts.GetValueOrDefault((int)teamType);
                foreach (int id in characterIds)
                {
                    if (!allowed.Contains(id))
                        throw new InvalidOperationException($"Support character {id} is unavailable for {teamType}");
                    if (content.UseValue > 0 && counts?.GetValueOrDefault(id) >= content.UseValue)
                        throw new InvalidOperationException($"Support character {id} has no daily uses left for {teamType}");
                }
            }

            // Replace abandoned attempts. Owned characters and unlimited contents need no counter.
            user.SupportCharacterBattles.Remove((int)teamType);
            if (characterIds.Count > 0 && content!.UseValue > 0)
                user.SupportCharacterBattles[(int)teamType] = new SupportCharacterBattle
                {
                    BattleId = battleId,
                    TeamNumber = teamNumber,
                    CharacterIds = characterIds
                };
        }
    }

    public static void FinishBattle(User user, TeamType teamType, int battleId, int teamNumber, int battleResult)
    {
        lock (user)
        {
            user.ResetDataIfNeeded();
            if (!user.SupportCharacterBattles.TryGetValue((int)teamType, out var battle) ||
                battle.BattleId != battleId || battle.TeamNumber != teamNumber)
                return;

            var content = GetContent(teamType);
            if (battleResult == 1 && content?.UseValue > 0)
            {
                if (!user.ResetableData.SupportCharacterUsedCounts.TryGetValue((int)teamType, out var counts))
                    user.ResetableData.SupportCharacterUsedCounts[(int)teamType] = counts = [];

                foreach (int id in battle.CharacterIds)
                    counts[id] = Math.Min(content.UseValue, counts.GetValueOrDefault(id) + 1);
            }
            // A repeated settlement cannot charge this attempt a second time.
            user.SupportCharacterBattles.Remove((int)teamType);
        }
    }

    public static TeamType GetTowerTeamType(int towerId)
    {
        var tower = GameData.Instance.towerTable.GetValueOrDefault(towerId)
            ?? throw new InvalidOperationException($"Unknown tower {towerId}");
        return tower.Type switch
        {
            CorporationTowerType.ALL => TeamType.TowerBase,
            CorporationTowerType.ELYSION => TeamType.TowerElysion,
            CorporationTowerType.MISSILIS => TeamType.TowerMissilis,
            CorporationTowerType.TETRA => TeamType.TowerTetra,
            CorporationTowerType.OVERSPEC => TeamType.TowerOverspec,
            _ => throw new InvalidOperationException($"Unknown tower type {tower.Type}")
        };
    }

    private static SupportContentsRecord? GetContent(TeamType teamType) =>
        GameData.Instance.SupportContentsTable.Values.FirstOrDefault(c => c.ContentsType == teamType);
}
