using EpinelPS.Data;

namespace EpinelPS.Services;

// NameCode identifies a character, independently of core upgrades and costumes.
// Only established mechanics are named here. New/unrecognized units stay neutral;
// sharing a squad, weapon or element is not automatically called skill synergy.
internal readonly record struct NpcPairBonus(int Score, string? Label, int BurstCaster = 0);

internal static class NpcSynergyRules
{
    internal static bool Heals(int name) => name is 5005 or 5008 or 5026 or 5027 or 5040 or 5048
        or 5051 or 5054 or 5064 or 5066 or 5099 or 5108 or 5114 or 5131 or 5134 or 5147;
    internal static bool ReducesCooldown(int name) => name is 5011 or 5036 or 5049 or 5061 or 5075 or 5098 or 5110 or 5137;
    internal static bool NeedsHealing(int name) => name is 5041 or 5095;
    internal static bool BurstHealing(int name) => name is 5066 or 5108 or 5131;
    private static bool PierceSniper(int name) => name is 5004 or 5001 or 5012 or 5101 or 5042;
    private static bool BurstAttackBuffer(int name) => name is 5011 or 5017 or 5104 or 5123 or 5147 or 5162;
    private static bool AttackBuffer(int name) => name is 5011 or 5017 or 5009 or 5036 or 5104
        or 5123 or 5131 or 5147 or 5151 or 5162 or 5178;

    internal static int Quality(int name) => name switch
    {
        5011 or 5065 or 5101 or 5105 or 5124 or 5129 => 24,
        5004 or 5044 or 5049 or 5099 or 5103 or 5110 or 5125 => 18,
        5001 or 5008 or 5009 or 5036 or 5075 or 5108 or 5117 => 12,
        _ => 0
    };

    internal static NpcPairBonus Pair(CharacterRecord a, WeaponType aw, CharacterRecord b, WeaponType bw)
    {
        int x = a.NameCode, y = b.NameCode;
        bool Is(int first, int second) => x == first && y == second || x == second && y == first;
        bool Has(int name) => x == name || y == name;
        int Other(int name) => x == name ? y : x;
        CharacterRecord OtherRow(int name) => x == name ? b : a;
        WeaponType OtherWeapon(int name) => x == name ? bw : aw;
        if (Is(5100, 5099)) return new(210, "蒂亚／娜嘉：掩体恢复与护盾增益", 5100);
        if (Has(5008) && Other(5008) is 5009 or 5049) return new(190, "777 同伴：布兰儿与同部队角色配合");
        if (Is(5116, 5117)) return new(190, "夏日樱花／罗珊娜：持续伤害增益", 5116);
        if (Is(5097, 5007)) return new(150, "夏日阿妮斯／普丽瓦蒂：弹匣与末弹配合");
        if (Has(5065) && Heals(Other(5065))) return new(135, "皇冠：通过队友治疗触发增益", BurstHealing(Other(5065)) ? Other(5065) : 0);
        if (Is(5004, 5105)) return new(115, "爱丽丝／红莲暗影：蓄力速度配合");
        if (Is(5001, 5004)) return new(105, "麦斯威尔／爱丽丝：蓄力与攻击增益");
        if (Has(5094) && Other(5094) is 5049 or 5064 or 5079 or 5134 or 5178)
            return new(100, "2B：最大生命增益配合", Other(5094) is 5079 or 5134 ? Other(5094) : 0);
        if (Has(5107) && Other(5107) is 5061 or 5105) return new(100, "伊莱格：分摊伤害增益");
        if (Has(5081) && OtherWeapon(5081) == WeaponType.SG) return new(85, "托比：霰弹枪队增益");
        if (Has(5092) && OtherWeapon(5092) == WeaponType.SG) return new(75, "莱昂纳：霰弹枪命中与暴击配合");
        if (Has(5167) && OtherWeapon(5167) == WeaponType.SG) return new(85, "命运伴侣：霰弹枪攻击增益", 5167);
        if (Has(5108) && OtherWeapon(5108) == WeaponType.RL && OtherRow(5108).Class == CharacterClassType.Attacker)
            return new(70, "雷姆：发射器增益与治疗", 5108);
        if (Has(5125) && Other(5125) is 5004 or 5001 or 5012 or 5101) return new(90, "格拉维：贯穿伤害配合");
        if (Has(5110) && OtherWeapon(5110) == WeaponType.SR && PierceSniper(Other(5110))) return new(65, "杀手妻子：贯穿狙击配合");
        if (Has(5151) && PierceSniper(Other(5151))) return new(85, "特工兔女郎：贯穿伤害增益");
        if (Has(5131) && Other(5131) is 5061 or 5094 or 5105) return new(90, "天真的女仆：分摊伤害增益");
        if (Is(5139, 5117)) return new(110, "莫莉／夏日樱花：持续伤害增益");
        if (Has(5030) && OtherWeapon(5030) is WeaponType.MG or WeaponType.SG) return new(35, "艾德米：换弹增益", 5030);
        if (Has(5009) && Other(5009) is 5097 or 5061) return new(-95, null); // Ammo expansion conflicts with last-bullet triggers.
        if (Heals(x) && NeedsHealing(y) || Heals(y) && NeedsHealing(x))
        {
            int healer = Heals(x) ? x : y;
            return new(95, "治疗：保障消耗生命的输出角色", BurstHealing(healer) ? healer : 0);
        }
        if (AttackBuffer(x) && b.Class == CharacterClassType.Attacker || AttackBuffer(y) && a.Class == CharacterClassType.Attacker)
        {
            int buffer = AttackBuffer(x) && b.Class == CharacterClassType.Attacker ? x : y;
            return new(35, "攻击辅助与输出配合", BurstAttackBuffer(buffer) ? buffer : 0);
        }
        if (ReducesCooldown(x) && b.Class == CharacterClassType.Attacker || ReducesCooldown(y) && a.Class == CharacterClassType.Attacker)
            return new(18, "冷却辅助与输出配合");
        return new(0, null);
    }
}
