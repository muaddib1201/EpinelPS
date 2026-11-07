using EpinelPS.Data;
using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Arena;

/// <summary>Independent arena equipment; cube ownership and levels remain in the inventory.</summary>
public static class ArenaHarmonyCubeService
{
    public static IReadOnlyList<NetUserHarmonyCubeData> Get(User user)
    {
        lock (JsonDb.SaveLock)
        {
            if (user.ArenaHarmonyCubeAssignments == null)
            {
                user.ArenaHarmonyCubeAssignments = Normalize(user, null);
                try { JsonDb.Save(); }
                catch
                {
                    user.ArenaHarmonyCubeAssignments = null;
                    throw;
                }
            }
            return Project(user, user.ArenaHarmonyCubeAssignments);
        }
    }

    /// <summary>Builds battle data without migrating or saving inside another arena transaction.</summary>
    public static IReadOnlyList<NetUserHarmonyCubeData> Snapshot(User user)
    {
        lock (JsonDb.SaveLock)
            return Project(user, user.ArenaHarmonyCubeAssignments);
    }

    internal static ResWearArenaHarmonyCube Wear(User user, ReqWearArenaHarmonyCube request)
    {
        var cubes = Mutate(user, assignments =>
        {
            var cube = RequireCube(user, request.Isn);
            var wear = request.Wear ?? throw Invalid("Wear data is required");
            ValidateWear(user, cube, wear);
            if (wear.SwapCsn > 0) assignments[cube.Isn].Remove(wear.SwapCsn);
            if (wear.Csn == 0) assignments[cube.Isn].Clear();
            else Assign(assignments, cube.Isn, wear.Csn);
            ValidateCapacity(cube, assignments[cube.Isn]);
        });
        var response = new ResWearArenaHarmonyCube();
        response.HarmonyCubes.AddRange(cubes);
        return response;
    }

    internal static ResClearArenaHarmonyCube Clear(User user, ReqClearArenaHarmonyCube request)
    {
        var cubes = Mutate(user, assignments =>
        {
            var cube = RequireCube(user, request.Isn);
            RequireCharacter(user, request.Csn, allowZero: true);
            if (request.Csn == 0) assignments[cube.Isn].Clear();
            else assignments[cube.Isn].Remove(request.Csn);
        });
        return new ResClearArenaHarmonyCube { HarmonyCube = cubes.Single(cube => cube.Isn == request.Isn) };
    }

    internal static ResManagementArenaHarmonyCube Manage(User user, ReqManagementArenaHarmonyCube request)
    {
        var cubes = Mutate(user, assignments =>
        {
            var cube = RequireCube(user, request.Isn);
            foreach (long csn in request.Clears) RequireCharacter(user, csn);
            foreach (var wear in request.Wears) ValidateWear(user, cube, wear);
            // Validate the batch's final target state after all removals and additions.
            foreach (long csn in request.Clears) assignments[cube.Isn].Remove(csn);
            if (request.Wears.Any(wear => wear.Csn == 0)) assignments[cube.Isn].Clear();
            foreach (var wear in request.Wears)
                if (wear.SwapCsn > 0) assignments[cube.Isn].Remove(wear.SwapCsn);
            foreach (var wear in request.Wears)
                if (wear.Csn > 0) Assign(assignments, cube.Isn, wear.Csn);
            ValidateCapacity(cube, assignments[cube.Isn]);
        });
        var response = new ResManagementArenaHarmonyCube();
        response.HarmonyCubes.AddRange(cubes);
        return response;
    }

    private static IReadOnlyList<NetUserHarmonyCubeData> Mutate(User user, Action<Dictionary<long, List<long>>> update)
    {
        lock (JsonDb.SaveLock)
        {
            var previous = user.ArenaHarmonyCubeAssignments;
            var candidate = Normalize(user, previous);
            update(candidate);
            user.ArenaHarmonyCubeAssignments = candidate;
            try { JsonDb.Save(); }
            catch
            {
                user.ArenaHarmonyCubeAssignments = previous;
                throw;
            }
            return Project(user, candidate);
        }
    }

    private static void Assign(Dictionary<long, List<long>> assignments, long isn, long csn)
    {
        foreach (var characters in assignments.Values) characters.Remove(csn);
        assignments[isn].Add(csn);
    }

    private static Dictionary<long, List<long>> Normalize(User user, Dictionary<long, List<long>>? assignments)
    {
        var result = new Dictionary<long, List<long>>();
        var assigned = new HashSet<long>();
        foreach (var cube in Cubes(user))
        {
            var record = GameData.Instance.ItemHarmonyCubeTable[cube.ItemType];
            int capacity = Capacity(cube, record);
            IEnumerable<long> source = assignments == null
                ? cube.CsnList.Concat([cube.Csn])
                : assignments.GetValueOrDefault(cube.Isn) ?? [];
            var characters = new List<long>();
            foreach (long csn in source)
            {
                if (characters.Count >= capacity) break;
                if (csn <= 0 || assigned.Contains(csn) || !Compatible(user, record, csn)) continue;
                characters.Add(csn);
                assigned.Add(csn);
            }
            result[cube.Isn] = characters;
        }
        return result;
    }

    private static IReadOnlyList<NetUserHarmonyCubeData> Project(User user, Dictionary<long, List<long>>? assignments)
    {
        var normalized = Normalize(user, assignments);
        var result = new List<NetUserHarmonyCubeData>();
        foreach (var cube in Cubes(user))
        {
            var net = new NetUserHarmonyCubeData { Isn = cube.Isn, Tid = cube.ItemType, Lv = cube.Level };
            net.CsnList.AddRange(normalized[cube.Isn]);
            result.Add(net);
        }
        return result;
    }

    private static IEnumerable<DbItemData> Cubes(User user) => user.Items
        .Where(item => item.Isn > 0 && GameData.Instance.ItemHarmonyCubeTable.ContainsKey(item.ItemType))
        .OrderBy(item => item.Isn).DistinctBy(item => item.Isn);

    private static DbItemData RequireCube(User user, long isn)
    {
        if (isn <= 0) throw Invalid("Invalid harmony cube serial number");
        var cube = user.Items.FirstOrDefault(item => item.Isn == isn)
            ?? throw new BadHttpRequestException("Harmony cube not found", 404);
        if (!GameData.Instance.ItemHarmonyCubeTable.TryGetValue(cube.ItemType, out var record))
            throw Invalid("Item is not a harmony cube");
        if (Capacity(cube, record) <= 0) throw Invalid("Harmony cube level has no valid slots");
        return cube;
    }

    private static void RequireCharacter(User user, long csn, bool allowZero = false)
    {
        if (csn < 0 || (csn == 0 && !allowZero)) throw Invalid("Invalid character serial number");
        if (csn > 0 && user.GetCharacterBySerialNumber(csn) == null)
            throw new BadHttpRequestException("Character not found", 404);
    }

    private static void ValidateWear(User user, DbItemData cube, NetWearHarmonyCubeData wear)
    {
        RequireCharacter(user, wear.Csn, allowZero: true);
        RequireCharacter(user, wear.SwapCsn, allowZero: true);
        if (wear.Csn > 0 && !Compatible(user, GameData.Instance.ItemHarmonyCubeTable[cube.ItemType], wear.Csn))
            throw Invalid("Character class is incompatible with harmony cube");
    }

    private static bool Compatible(User user, ItemHarmonyCubeRecord cube, long csn) =>
        user.GetCharacterBySerialNumber(csn) is { } character &&
        GameData.Instance.CharacterTable.TryGetValue(character.Tid, out var record) &&
        (cube.Class == CharacterClassType.All || cube.Class == record.Class);

    private static int Capacity(DbItemData cube, ItemHarmonyCubeRecord record) =>
        GameData.Instance.ItemHarmonyCubeLevelTable.Values.FirstOrDefault(level =>
            level.LevelEnhanceId == record.LevelEnhanceId && level.Level == cube.Level)?.Slot ?? 0;

    private static void ValidateCapacity(DbItemData cube, List<long> assignments)
    {
        int capacity = Capacity(cube, GameData.Instance.ItemHarmonyCubeTable[cube.ItemType]);
        if (assignments.Count > capacity) throw Invalid($"Harmony cube slot limit is {capacity}");
    }

    private static BadHttpRequestException Invalid(string message) => new(message, 400);
}
