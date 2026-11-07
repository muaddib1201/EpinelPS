using EpinelPS.Data;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Character;

internal static class CharacterUpgradeService
{
    internal static ResCharacterUpgrade Upgrade(User user, long csn, long isn, int count)
    {
        if (count <= 0) throw new BadHttpRequestException("Upgrade quantity must be positive.", 400);
        var character = user.GetCharacterBySerialNumber(csn)
            ?? throw new BadHttpRequestException("Character is not owned.", 400);
        if (!GameData.Instance.CharacterTable.TryGetValue(character.Tid, out var current))
            throw new BadHttpRequestException("Unknown character template.", 400);
        var next = GameData.Instance.CharacterTable.Values.FirstOrDefault(c => c.NameCode == current.NameCode &&
            (long)c.GradeCoreId == (long)current.GradeCoreId + count)
            ?? throw new BadHttpRequestException("The requested upgrade exceeds this character's limit.", 400);
        var body = user.Items.FirstOrDefault(i => i.Isn == isn);
        if (current.PieceId <= 0 || body == null || body.ItemType != current.PieceId || body.Count < count)
            throw new BadHttpRequestException("Insufficient matching spare bodies.", 400);

        body.Count -= count;
        character.Tid = next.Id;
        character.Grade = next.GradeCoreId - RewardUtils.GetValueByRarity(current.OriginalRare, 201, 101, 1);
        user.AddTrigger(Trigger.CharacterGradeUpCount, count);
        if (next.GradeCoreId is 103 or 11 or 201) user.AddTrigger(Trigger.CharacterGradeMax, 1);
        return new ResCharacterUpgrade
        {
            Character = new NetUserCharacterDefaultData { Csn = csn, Tid = character.Tid, Grade = character.Grade,
                Lv = user.GetCharacterLevel(character.Csn, character.Level), CostumeId = character.CostumeId,
                Skill1Lv = character.Skill1Lvl, Skill2Lv = character.Skill2Lvl, UltiSkillLv = character.UltimateLevel },
            Items = { NetUtils.ToNet(body) }
        };
    }
}
