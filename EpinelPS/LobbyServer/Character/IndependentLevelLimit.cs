using EpinelPS.Data;

namespace EpinelPS.LobbyServer.Character;

internal static class IndependentLevelLimit
{
    internal static void Validate(CharacterModel character, int targetLevel)
    {
        if (!GameData.Instance.CharacterTable.TryGetValue(character.Tid, out var definition))
            throw new BadHttpRequestException("Unknown character template.", 400);

        if (definition.OriginalRare != OriginalRareType.SSR) return;

        // SSR template stages start at 1; stage 4 is limit break 3, followed by cores.
        int maximumLevel = definition.GradeCoreId >= 4 ? 200 : 160;
        if (targetLevel > maximumLevel)
            throw new BadHttpRequestException($"This SSR's independent level cannot exceed {maximumLevel}.", 400);
    }
}
