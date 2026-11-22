using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Inventory;

[GameRequest("/inventory/equipment/changeoption")]
public class ChangeOption : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        var req = await ReadData<ReqAwakeningChangeOption>();
        var user = GetUser();
        var response = new ResAwakeningChangeOption();
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!ReferenceEquals(JsonDb.GetUser(user.ID), user))
                throw new BadHttpRequestException("Stale equipment inventory.", 409);

            var entries = user.EquipmentAwakenings;
            var current = entries.FirstOrDefault(x => x.Isn == req.Isn && !x.IsNewData);
            var pending = entries.FirstOrDefault(x => x.Isn == req.Isn && x.IsNewData);
            if (current != null && pending != null)
            {
                var chosen = req.IsChanged ? pending.Option : current.Option;
                var original = current.Option;
                try
                {
                    current.Option = chosen;
                    user.EquipmentAwakenings = entries.Where(x => x.Isn != req.Isn || x == current).ToList();
                    JsonDb.Save();
                }
                catch
                {
                    current.Option = original;
                    user.EquipmentAwakenings = entries;
                    throw;
                }
            }
            if (current != null)
                response.Awakening = new NetEquipmentAwakening { Isn = req.Isn, Option = current.Option.Clone() };
        }
        await WriteDataAsync(response);
    }
}
