using EpinelPS.Data;
using EpinelPS.Database;

namespace EpinelPS.LobbyServer.Inventory;

[GameRequest("/inventory/equipment/upgradeoption")]
public class UpgradeOption : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqAwakeningUpgradeOption req = await ReadData<ReqAwakeningUpgradeOption>();
        var response = new ResAwakeningUpgradeOption();
        response.ResetOption = EquipmentOptionRoll.Roll(GetUser(), req.Isn,
            [req.Slot1, req.Slot2, req.Slot3], response.Items, (_, currentId, _) => currentId == 0 ? 0 : GenerateNewOptionId(currentId));
        await WriteDataAsync(response);
    }

    private int GenerateNewOptionId(int currentStateEffectId)
    {
        EquipmentOptionRecord? currentOption = GameData.Instance.EquipmentOptionTable.Values
            .FirstOrDefault(option => option.StateEffectList != null && option.StateEffectList.Any(se => se.StateEffectId == currentStateEffectId));

        if (currentOption == null || currentOption.EquipmentOptionGroupId != 100000)
        {
            throw new InvalidOperationException($"Current state_effect_id {currentStateEffectId} not found in any EquipmentOption");
        }


        int stateEffectGroupId = currentOption.StateEffectGroupId;

        List<EquipmentOptionRecord> optionsInGroup = GameData.Instance.EquipmentOptionTable.Values
            .Where(x => x.EquipmentOptionGroupId == 100000 && x.StateEffectGroupId == stateEffectGroupId)
            .ToList();

        if (optionsInGroup.Count == 0)
        {
            throw new InvalidOperationException($"No awakening options found with state_effect_group_id {stateEffectGroupId}");
        }

        if (JsonDb.Instance.ValueResetTierRates is { } rates)
            return EquipmentOptionRoll.EffectAtLevel(optionsInGroup, ValueResetTiers.DrawLevel(rates, Random.Shared));
        return SelectOptionFromGroup(optionsInGroup);
    }


    private static readonly Random _random = new();

    private int SelectOptionFromGroup(List<EquipmentOptionRecord> options)
    {
        if (options == null || options.Count == 0)
            throw new InvalidOperationException("No options available in group - this indicates a data consistency issue");

        long totalRatio = options.Sum(x => (long)x.OptionRatio);

        long randomValue = _random.NextInt64(0, totalRatio);
        long cumulativeRatio = 0;

        foreach (EquipmentOptionRecord option in options)
        {
            cumulativeRatio += option.OptionRatio;
            if (randomValue < cumulativeRatio)
            {
                // Randomly select from the StateEffectList
                if (option.StateEffectList == null || option.StateEffectList.Count == 0)
                {
                    throw new InvalidOperationException($"StateEffectList is null or empty for option {option.Id}");
                }
                int randomIndex = _random.Next(option.StateEffectList.Count);
                return option.StateEffectList[randomIndex].StateEffectId;
            }
        }

        // Fallback: randomly select from the StateEffectList of the last option
        EquipmentOptionRecord? lastOption = options.Last();
        if (lastOption?.StateEffectList == null || lastOption.StateEffectList.Count == 0)
        {
            throw new InvalidOperationException($"StateEffectList is null or empty for fallback option {lastOption?.Id}");
        }
        int fallbackIndex = _random.Next(lastOption.StateEffectList.Count);
        return lastOption.StateEffectList[fallbackIndex].StateEffectId;
    }
}
