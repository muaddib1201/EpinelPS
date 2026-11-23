using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Inventory;

[GameRequest("/inventory/equipment/unlockoption")]
public class UnlockOption : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqAwakeningUnlockOption>();
        if (req.Slot is < 1 or > 3)
            throw new BadHttpRequestException("Invalid equipment option slot.", StatusCodes.Status400BadRequest);

        var user = GetUser();
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!ReferenceEquals(JsonDb.GetUser(user.ID), user))
                throw new BadHttpRequestException("Stale equipment inventory.", StatusCodes.Status409Conflict);

            var entries = user.EquipmentAwakenings.Where(entry => entry.Isn == req.Isn).ToArray();
            if (!entries.Any(entry => !entry.IsNewData))
                throw new BadHttpRequestException("Awakened equipment not found.", StatusCodes.Status404NotFound);

            // Keeping or accepting a pending comparison must not restore the old lock.
            var originals = new List<(EquipmentAwakeningData Entry, NetEquipmentAwakeningOption Option)>();
            try
            {
                foreach (var entry in entries)
                {
                    var option = entry.Option.Clone();
                    switch (req.Slot)
                    {
                        case 1: option.Option1Lock = option.IsOption1DisposableLock = false; break;
                        case 2: option.Option2Lock = option.IsOption2DisposableLock = false; break;
                        case 3: option.Option3Lock = option.IsOption3DisposableLock = false; break;
                    }
                    if (option.Equals(entry.Option)) continue;
                    originals.Add((entry, entry.Option));
                    entry.Option = option;
                }
                if (originals.Count > 0) JsonDb.Save();
            }
            catch
            {
                foreach (var (entry, original) in originals) entry.Option = original;
                throw;
            }
        }
        await WriteDataAsync(new ResAwakeningUnlockOption());
    }
}
