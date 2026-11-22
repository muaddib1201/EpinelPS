using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Inventory;

[GameRequest("/inventory/equipment/resetoption")]
public class ResetOption : LobbyMessage
{
    protected override async Task HandleAsync()
    {
        ReqAwakeningResetOption req = await ReadData<ReqAwakeningResetOption>();
        var response = new ResAwakeningResetOption();
        response.ResetOption = EquipmentOptionRoll.Roll(GetUser(), req.Isn,
            [req.Slot1, req.Slot2, req.Slot3], response.Items, RollEffect);
        await WriteDataAsync(response);
    }

    private int RollEffect(int slot, int currentId, List<int> excluded)
    {
        double chance = slot switch { 0 => 1.0, 1 => 0.5, _ => 0.3 };
        return Random.Shared.NextDouble() < chance ? GenerateNewOptionIdWithDynamicProbability(excluded) : 0;
    }

    private int GenerateNewOptionIdWithDynamicProbability(List<int> excludedStateEffectIds)
    {
        // Get all awakening options (equipment_option_group_id == 100000)
        List<EquipmentOptionRecord> allAwakeningOptions = GameData.Instance.EquipmentOptionTable.Values
            .Where(x => x.EquipmentOptionGroupId == 100000)
            .ToList();

        // Filter out options that have already been taken (non-repeating principle)
        HashSet<int> excludedEffectGroupIds = new();

        // Get the effect group IDs for all excluded state effect IDs
        foreach (int stateEffectId in excludedStateEffectIds)
        {
            EquipmentOptionRecord? excludedOption = GameData.Instance.EquipmentOptionTable.Values
                .FirstOrDefault(opt => opt.StateEffectList != null && opt.StateEffectList.Any(se => se.StateEffectId == stateEffectId));
            if (excludedOption != null)
            {
                excludedEffectGroupIds.Add(excludedOption.StateEffectGroupId);
            }
        }

        List<EquipmentOptionRecord> availableOptions = allAwakeningOptions
            .Where(option => !excludedEffectGroupIds.Contains(option.StateEffectGroupId))
            .ToList();

        Dictionary<int, List<EquipmentOptionRecord>> optionsByEffectGroup = availableOptions
            .GroupBy(option => option.StateEffectGroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        double excludedProbabilitySum = CalculateExcludedProbabilitySumByEffectGroup(excludedEffectGroupIds);

        List<EffectGroupWithWeight> weightedEffectGroups = CalculateDynamicProbabilitiesForEffectGroups(optionsByEffectGroup, excludedProbabilitySum);
        int selectedEffectGroupId = SelectWeightedRandomEffectGroup(weightedEffectGroups);

        List<EquipmentOptionRecord> optionsInSelectedGroup = optionsByEffectGroup[selectedEffectGroupId];
        int selectedStateEffectId = SelectOptionFromGroup(optionsInSelectedGroup);
        return selectedStateEffectId;
    }



    /// <summary>
    /// Helper class to store effect group with its calculated weight for probability selection
    /// </summary>
    public class EffectGroupWithWeight
    {
        public int EffectGroupId { get; set; }
        public double Weight { get; set; }
        public double BaseProbability { get; set; }
        public double DynamicProbability { get; set; }
    }

    /// <summary>
    /// Calculates the sum of base probabilities for excluded effect groups according to the Overload system rules
    /// </summary>
    /// <param name="excludedEffectGroupIds">List of excluded state_effect_group_ids</param>
    /// <returns>Sum of base probabilities (as decimal percentage)</returns>
    private double CalculateExcludedProbabilitySumByEffectGroup(HashSet<int> excludedEffectGroupIds)
    {
        if (excludedEffectGroupIds.Count == 0)
        {
            return 0.0;
        }

        double totalExcluded = 0.0;

        foreach (int effectGroupId in excludedEffectGroupIds)
        {
            List<EquipmentOptionRecord> options = GameData.Instance.EquipmentOptionTable.Values
                .Where(opt => opt.StateEffectGroupId == effectGroupId && opt.EquipmentOptionGroupId == 100000)
                .ToList();

            if (options.Count > 0)
            {
                EquipmentOptionRecord firstOption = options.First();
                totalExcluded += firstOption.OptionGroupRatio / 100.0;
            }
        }

        // Cap the excluded probability sum to maintain mathematical validity
        return Math.Min(totalExcluded, 99.9); // Ensure we don't reach 100% to avoid division by zero
    }

    /// <summary>
    /// Calculates dynamic probabilities for available effect groups using the formula:
    /// Dynamic Probability = Display Probability / (100% - Sum of Excluded Probabilities)
    /// </summary>
    /// <param name="optionsByEffectGroup">Dictionary of available options grouped by effect group ID</param>
    /// <param name="excludedProbabilitySum">Sum of probabilities of excluded effects</param>
    /// <returns>List of weighted effect groups for random selection</returns>
    private List<EffectGroupWithWeight> CalculateDynamicProbabilitiesForEffectGroups(Dictionary<int, List<EquipmentOptionRecord>> optionsByEffectGroup, double excludedProbabilitySum)
    {
        List<EffectGroupWithWeight> weightedEffectGroups = new();

        double probabilityDenominator = 100.0 - excludedProbabilitySum;

        // Prevent division by zero or negative values (shouldn't happen due to capping in CalculateExcludedProbabilitySumByEffectGroup, but let's be safe)
        if (probabilityDenominator <= 0)
        {
            Logging.WriteLine($"Warning: probabilityDenominator is {probabilityDenominator}, using uniform distribution", LogType.Warning);
            probabilityDenominator = 1.0; // Use uniform distribution as fallback
        }

        foreach (KeyValuePair<int, List<EquipmentOptionRecord>> kvp in optionsByEffectGroup)
        {
            int effectGroupId = kvp.Key;
            List<EquipmentOptionRecord> options = kvp.Value;

            if (options.Count > 0)
            {
                EquipmentOptionRecord firstOption = options.First();
                double baseProbability = firstOption.OptionGroupRatio / 100.0;

                double dynamicProbability = baseProbability / probabilityDenominator;

                double selectionWeight = dynamicProbability * 1000000;

                weightedEffectGroups.Add(new EffectGroupWithWeight
                {
                    EffectGroupId = effectGroupId,
                    Weight = selectionWeight,
                    BaseProbability = baseProbability,
                    DynamicProbability = dynamicProbability
                });
            }
        }

        return weightedEffectGroups;
    }

    /// <summary>
    /// Selects an effect group randomly based on calculated weights
    /// </summary>
    /// <param name="weightedEffectGroups">List of weighted effect groups</param>
    /// <returns>Selected effect_group_id or 0 if none available</returns>
    private int SelectWeightedRandomEffectGroup(List<EffectGroupWithWeight> weightedEffectGroups)
    {
        // Safety check to prevent data corruption
        if (weightedEffectGroups == null || weightedEffectGroups.Count == 0)
            throw new InvalidOperationException("No weighted effect groups available - this indicates a data consistency issue");

        Random random = new();
        double totalWeight = weightedEffectGroups.Sum(weg => weg.Weight);

        // Prevent division by zero which could cause unexpected behavior
        if (totalWeight <= 0)
            throw new InvalidOperationException("Invalid group weights - this indicates a data consistency issue");

        double randomValue = random.NextDouble() * totalWeight;

        double cumulativeWeight = 0.0;
        foreach (EffectGroupWithWeight weightedGroup in weightedEffectGroups)
        {
            cumulativeWeight += weightedGroup.Weight;
            if (randomValue <= cumulativeWeight)
            {
                return weightedGroup.EffectGroupId;
            }
        }

        return weightedEffectGroups.Last().EffectGroupId;
    }

    private static readonly Random _random = new();

    /// <summary>
    /// Selects an option from an effect group based on option_ratio weights and returns a state_effect_id
    /// </summary>
    /// <param name="options">List of options in the effect group</param>
    /// <returns>Selected state_effect_id</returns>
    private int SelectOptionFromGroup(List<EquipmentOptionRecord> options)
    {
        // Safety check to prevent data corruption
        if (options == null || options.Count == 0)
            throw new InvalidOperationException("No options available in group - this indicates a data consistency issue");

        if (JsonDb.Instance.EffectChangeBands is { } bands)
        {
            int level = bands.DrawLevel(Random.Shared);
            var matches = options.SelectMany(option => option.StateEffectList)
                .Where(effect => effect.StateEffectLevel == level).ToArray();
            if (matches.Length == 0) throw new InvalidDataException($"Missing equipment effect level {level}");
            return matches[Random.Shared.Next(matches.Length)].StateEffectId;
        }

        long totalRatio = options.Sum(x => (long)x.OptionRatio);

        long randomValue = _random.NextInt64(0, totalRatio);
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
