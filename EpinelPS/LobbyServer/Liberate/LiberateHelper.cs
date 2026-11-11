using EpinelPS.Data;

namespace EpinelPS.LobbyServer.Liberate;

public class LiberateHelper
{
    private const int MissionSlots = 3;

    /// <summary>Repairs the client slots without renewing a completed liberation or changing its rewards.</summary>
    public static NetLiberateData ReconcileMissions(NetLiberateData stored)
    {
        var data = GameData.Instance;
        if (!data.LiberateCharacterTable.TryGetValue(stored.CharacterId, out var character))
            throw new InvalidDataException("Unknown liberation character.");
        var group = data.LiberateMissionGroupTable.Values.FirstOrDefault(g =>
            g.MissionStepGroup == character.MissionStepGroup && g.MissionStep == stored.Step)
            ?? throw new InvalidDataException("Unknown liberation stage.");

        var visible = stored.MissionData.Where(m => m.Id is >= 1 and <= MissionSlots).ToArray();
        var expired = !stored.IsCompleted && visible.Any(m => m.CreatedAt <= DateTime.UtcNow.AddHours(-24).Ticks);
        bool Valid(NetLiberateMissionData mission) => mission.LiberateCharacterId == stored.CharacterId &&
            data.LiberateMissionTable.TryGetValue(mission.MissionTid, out var row) &&
            (row.GroupId == group.DefaultMissionGroupId || row.GroupId == group.ResetMissionGroupId);

        if (!expired && stored.MissionData.Count == MissionSlots && visible.Length == MissionSlots &&
            visible.Select(m => m.Id).Distinct().Count() == MissionSlots &&
            visible.Select(m => m.MissionTid).Distinct().Count() == MissionSlots && visible.All(Valid))
            return stored;

        var repaired = stored.Clone();
        repaired.MissionData.Clear();
        if (expired || stored.MissionData.Count == 0)
        {
            repaired.MissionData.AddRange(CreateDailyMissions(stored.CharacterId, group));
            if (!stored.IsCompleted) repaired.RewardedCount = 0;
            return repaired;
        }

        // The legacy list contains the reset candidate pool. Retain the three slots
        // the client actually displayed, including any already claimed mission.
        List<NetLiberateMissionData>? replacements = null;
        var used = new HashSet<int>();
        for (int slot = 1; slot <= MissionSlots; slot++)
        {
            var mission = visible.FirstOrDefault(m => m.Id == slot && Valid(m) && !used.Contains(m.MissionTid));
            if (mission == null)
            {
                replacements ??= CreateDailyMissions(stored.CharacterId, group);
                mission = replacements.First(m => !used.Contains(m.MissionTid));
            }
            var retained = mission.Clone();
            retained.Id = slot;
            used.Add(retained.MissionTid);
            repaired.MissionData.Add(retained);
        }
        return repaired;
    }

    private static List<NetLiberateMissionData> CreateDailyMissions(int characterId, LiberateMissionGroupRecord group)
    {
        var subgroups = GameData.Instance.LiberateMissionTable.Values
            .Where(m => m.GroupId == group.DefaultMissionGroupId)
            .GroupBy(m => m.SubGroupId).Where(g => g.Count() == MissionSlots).Select(g => g.Key).ToArray();
        if (subgroups.Length == 0) throw new InvalidDataException("No three-slot liberation mission group.");
        return GetMissions(characterId, group.DefaultMissionGroupId, subgroups[Random.Shared.Next(subgroups.Length)]);
    }

    public static List<NetLiberateMissionData> GetMissions(int characterId, int missgroup, int randomSubGroupId)
    {
        List<LiberateMissionRecord>? missions = GameData.Instance.LiberateMissionTable.Values
        .Where(x => x.GroupId == missgroup && x.SubGroupId == randomSubGroupId)
        .ToList();

        List<NetLiberateMissionData> netLiberates = new();

        if (missions.Count > 0)
        {
            int id = 1;
            foreach (var item in missions)
            {
                netLiberates.Add(new NetLiberateMissionData()
                {
                    LiberateCharacterId = characterId,
                    Id = id,
                    MissionState = LiberateMissionState.Running,
                    MissionTid = item.Id,
                    CreatedAt = DateTime.UtcNow.Ticks,
                    ReceivedAt = DateTime.UtcNow.Ticks,
                    TriggerStartAt = DateTime.UtcNow.Ticks,
                    TriggerEndAt = DateTime.UtcNow.AddHours(24).Ticks,
                });
                id++;
            }

        }

        return netLiberates;
    }




}
