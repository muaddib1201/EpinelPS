using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Character;

[GameRequest("/character/synchrodevice/addslot")]
public class SynchroAddSlot : LobbyMessage
{
    public const int BaseSlotCount = 5;
    public const int SlotPriceGems = 500;

    /// <summary>
    /// Slots the user may hold without paying gems: the 5 initial slots plus the
    /// Tactics Academy "Synchro Device" lessons (5 slots each) and slots opened with items.
    /// </summary>
    public static int GetFreeSlotAllowance(User user)
    {
        int allowance = BaseSlotCount + user.SynchroSlotsFromItems;
        foreach (int lessonId in user.CompletedTacticAcademyLessons)
        {
            if (GameData.Instance.TacticAcademyLessons.TryGetValue(lessonId, out TacticAcademyFunctionRecord? lesson)
                && lesson.LessonType == LessonType.SynchroDevice && lesson.LessonReward != null)
            {
                allowance += lesson.LessonReward.Sum(r => r.LessonRewardValue);
            }
        }
        return allowance;
    }

    public static SynchroSlot AppendSlot(User user)
    {
        SynchroSlot slot = new()
        {
            Slot = user.SynchroSlots.Count == 0 ? 1 : user.SynchroSlots.Max(s => s.Slot) + 1,
            CharacterSerialNumber = 0,
            AvailableAt = 0
        };
        user.SynchroSlots.Add(slot);
        return slot;
    }

    protected override async Task HandleAsync()
    {
        ReqSynchroAddSlot req = await ReadData<ReqSynchroAddSlot>();
        User user = GetUser();
        ResSynchroAddSlot response;
        lock (user) response = BuySlot(user, req.Slot);
        await WriteDataAsync(response);
    }

    private static ResSynchroAddSlot BuySlot(User user, int requestedSlot)
    {
        ResSynchroAddSlot response = new();

        SynchroSlot? slot = user.SynchroSlots.FirstOrDefault(x => x.Slot == requestedSlot);
        if (slot == null)
        {
            // Slots beyond the free allowance are bought with gems, as on the official server.
            if (user.SynchroSlots.Count >= GetFreeSlotAllowance(user))
            {
                if (!user.CanSubtractCurrency(CurrencyType.FreeCash, SlotPriceGems))
                {
                    Logging.WriteLine($"User {user.ID} cannot afford synchro slot {requestedSlot} ({SlotPriceGems} gems)", LogType.Warning);
                    return response;
                }
                user.SubtractCurrency(CurrencyType.FreeCash, SlotPriceGems);
            }

            slot = AppendSlot(user);
            JsonDb.Save();
        }

        response.Slot = new NetSynchroSlot
        {
            Csn = slot.CharacterSerialNumber,
            Slot = slot.Slot,
            AvailableRegisterAt = slot.AvailableAt
        };
        foreach (KeyValuePair<CurrencyType, long> currency in user.Currency)
            response.Currencies.Add(new NetUserCurrencyData() { Type = (int)currency.Key, Value = currency.Value });

        return response;
    }
}
