using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Character;

[GameRequest("/character/synchrodevice/addslotbyitem")]
public class SynchroAddSlotByItem : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        // Broken protocol so we dIdn't validate request data.
        // May fix later.
        ReqSynchroAddSlot req = await ReadData<ReqSynchroAddSlot>();

        User user = GetUser();
        ResSynchroAddSlot response = new();

        SynchroSlot slot = SynchroAddSlot.AppendSlot(user);
        user.SynchroSlotsFromItems++;

        response.Slot = new NetSynchroSlot()
        {
            Csn = slot.CharacterSerialNumber,
            Slot = slot.Slot,
            AvailableRegisterAt = slot.AvailableAt
        };

        JsonDb.Save();

        await WriteDataAsync(response);
    }
}
