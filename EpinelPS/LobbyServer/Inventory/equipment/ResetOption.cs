using EpinelPS.Data;
using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Inventory;

[GameRequest("/inventory/equipment/resetoption")]
public class ResetOption : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqAwakeningResetOption req = await ReadData<ReqAwakeningResetOption>();
        var response = new ResAwakeningResetOption();
        EffectChangeSlotRates? settings = null;
        response.ResetOption = EquipmentOptionRoll.Roll(GetUser(), req.Isn,
            [req.Slot1, req.Slot2, req.Slot3], response.Items, rollEmptySlots: true,
            (slot, _, excluded) => RollEffect(slot, excluded, settings ??= JsonDb.Instance.EffectChangeSlotRates ?? EffectChangeSlotRates.Defaults()));
        await WriteDataAsync(response);
    }

    private static int RollEffect(int slot, List<int> excluded, EffectChangeSlotRates settings)
    {
        if (!settings.Appears(slot, Random.Shared)) return 0;

        var groups = GameData.Instance.EquipmentOptionTable.Values
            .Where(x => x.EquipmentOptionGroupId == EffectChangeSlotRates.OverloadGroup)
            .ToLookup(x => x.StateEffectGroupId);
        var excludedGroups = groups.Where(group => group.Any(option =>
                option.StateEffectList.Any(effect => excluded.Contains(effect.StateEffectId))))
            .Select(group => group.Key).ToHashSet();
        int groupId = settings.DrawGroup(slot, excludedGroups, Random.Shared);
        return groupId == 0 ? 0 : SelectOptionFromGroup(groups[groupId].ToList());
    }

    /// <summary>
    /// Selects an option from an effect group based on option_ratio weights and returns a state_effect_id
    /// </summary>
    /// <param name="options">List of options in the effect group</param>
    /// <returns>Selected state_effect_id</returns>
    private static int SelectOptionFromGroup(List<EquipmentOptionRecord> options)
    {
        // Safety check to prevent data corruption
        if (options == null || options.Count == 0)
            throw new InvalidOperationException("No options available in group - this indicates a data consistency issue");

        if (JsonDb.Instance.EffectChangeBands is { } bands)
        {
            int level = bands.DrawLevel(Random.Shared);
            return EquipmentOptionRoll.EffectAtLevel(options, level);
        }

        long totalRatio = options.Sum(x => (long)x.OptionRatio);

        long randomValue = Random.Shared.NextInt64(0, totalRatio);
        long cumulativeRatio = 0;

        foreach (EquipmentOptionRecord? option in options)
        {
            cumulativeRatio += option.OptionRatio;
            if (randomValue < cumulativeRatio)
            {
                // Randomly select from the StateEffectList
                if (option.StateEffectList == null || option.StateEffectList.Count == 0)
                {
                    throw new InvalidOperationException($"StateEffectList is null or empty for option {option.Id}");
                }
                int randomIndex = Random.Shared.Next(option.StateEffectList.Count);
                return option.StateEffectList[randomIndex].StateEffectId;
            }
        }

        // Fallback: randomly select from the StateEffectList of the last option
        EquipmentOptionRecord? lastOption = options.Last();
        if (lastOption?.StateEffectList == null || lastOption.StateEffectList.Count == 0)
        {
            throw new InvalidOperationException($"StateEffectList is null or empty for fallback option {lastOption?.Id}");
        }
        int fallbackIndex = Random.Shared.Next(lastOption.StateEffectList.Count);
        return lastOption.StateEffectList[fallbackIndex].StateEffectId;
    }
}
