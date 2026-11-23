using EpinelPS.Data;

namespace EpinelPS.Models;

public sealed class EffectChangeSlotRates
{
    public const int SlotCount = 3;
    public const int OverloadGroup = 100000;
    // Positions in this catalogue also identify the columns in saved probability arrays.
    public static IReadOnlyList<(int GroupId, string Name)> EffectTypes { get; } =
    [
        (100100, "优越代码伤害"), (100200, "命中率"), (100300, "最大装弹数"),
        (100400, "攻击力"), (100500, "蓄力伤害"), (100600, "蓄力速度"),
        (100700, "暴击率"), (100800, "暴击伤害"), (100900, "防御力")
    ];

    public decimal[] Appearance { get; set; } = [];
    public decimal[][] Effects { get; set; } = [];

    public static EffectChangeSlotRates Defaults()
    {
        var groups = GameData.Instance.EquipmentOptionTable.Values
            .Where(x => x.EquipmentOptionGroupId == OverloadGroup).ToLookup(x => x.StateEffectGroupId);
        decimal[] weights = EffectTypes.Select(type => (decimal)groups[type.GroupId].First().OptionGroupRatio).ToArray();
        return new EffectChangeSlotRates { Appearance = [100, 50, 30], Effects = [[.. weights], [.. weights], [.. weights]] };
    }

    public bool IsValid() =>
        Appearance is { Length: SlotCount } && Appearance.All(IsPercent) &&
        Effects is { Length: SlotCount } && Effects.All(row => row?.Length == EffectTypes.Count &&
            row.All(IsPercent) && row.Sum() == 100m);

    private static bool IsPercent(decimal value) => value >= 0m && value <= 100m && decimal.Round(value, 2) == value;

    public bool Appears(int slot, Random random) => random.Next(10000) < Appearance[slot] * 100m;

    public int DrawGroup(int slot, IReadOnlySet<int> excluded, Random random)
    {
        var available = EffectTypes.Select((type, index) =>
                (type.GroupId, Tickets: (int)(Effects[slot][index] * 100m)))
            .Where(x => x.Tickets > 0 && !excluded.Contains(x.GroupId)).ToArray();
        if (available.Length == 0) return 0;

        int ticket = random.Next(available.Sum(x => x.Tickets));
        foreach (var group in available)
        {
            if (ticket < group.Tickets) return group.GroupId;
            ticket -= group.Tickets;
        }
        throw new InvalidOperationException("Equipment effect ticket exceeded the available weight.");
    }
}
