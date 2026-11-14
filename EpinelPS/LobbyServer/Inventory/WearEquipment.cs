using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Inventory;

[GameRequest("/inventory/wearequipment")]
public class WearEquipment : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqWearEquipment req = await ReadData<ReqWearEquipment>();
        User user = GetUser();

        ResWearEquipment response = new();

        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!ReferenceEquals(JsonDb.GetUser(user.ID), user))
                throw new BadHttpRequestException("Stale equipment inventory.", 409);

            DbItemData target = user.Items.FirstOrDefault(item => item.Isn == req.Isn)
                ?? throw new BadHttpRequestException("Equipment not found.", 404);
            if (target.Count <= 0)
                throw new BadHttpRequestException("Equipment stack is empty.", 400);
            if (!user.Characters.Any(character => character.Csn == req.Csn) || req.Csn == 0)
                throw new BadHttpRequestException("Character not found.", 404);

            int pos = NetUtils.GetItemPos(user, req.Isn);
            List<DbItemData> updatedItems = [];

            foreach (DbItemData item in user.Items)
            {
                if (item.Csn == req.Csn && item.Position == pos)
                {
                    item.Csn = 0;
                    item.Position = 0;
                    updatedItems.Add(item);
                }
            }

            // Equip one instance, as the quick-equip endpoint does, and report
            // both the remaining stack and the newly equipped instance.
            if (target.Count > 1)
            {
                target.Count--;
                updatedItems.Add(target);
                target = new DbItemData
                {
                    ItemType = target.ItemType,
                    Isn = user.GenerateUniqueItemId(),
                    Count = 1,
                    Level = target.Level,
                    Exp = target.Exp,
                    Corp = target.Corp
                };
                user.Items.Add(target);
            }

            target.Csn = req.Csn;
            target.Position = pos;
            updatedItems.Add(target);

            // The client applies item deltas. Omitted unequipped items remain
            // equipped in its cache; each changed item needs its final state.
            response.Items.AddRange(updatedItems.DistinctBy(item => item.Isn).Select(NetUtils.ToNet));
            JsonDb.Save();
        }

        await WriteDataAsync(response);
    }
}
