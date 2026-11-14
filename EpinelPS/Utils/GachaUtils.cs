using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Utils;
using EpinelPS.LobbyServer.Event;
using Org.BouncyCastle.Ocsp;

namespace EpinelPS.Utils;

public class GachaUtils
{
    private static readonly Random random = new();


    /// <summary>
    /// Performs gacha pulls based on the GachaTypeRecord. Handles rates for groups (R, SR, SSR, etc) and attached character lists.
    /// </summary>
    /// <param name="gachaType"></param>
    /// <param name="numberOfPulls"></param>
    /// <param name="user"></param>
    /// <returns></returns>
    public static List<CharacterRecord> ExecuteGachaPull(GachaTypeRecord gachaType, int numberOfPulls, User user)
    {
        // Fill the wishlist anyway. It won't be used of not compatible with the banner.
        List<CharacterRecord> wishlistCharacters = user.GetWishlistCharacters(gachaType.Id);

        List<CharacterRecord> selectedCharacters = [];

        if (user.CustomGachaRates != null && gachaType.Type is not (GachaPremiumType.GachaTutorial or GachaPremiumType.GachaFriend))
            Logging.WriteLine($"Using custom gacha rates: {user.CustomGachaRates}");

        if (gachaType.Type == GachaPremiumType.GachaTutorial)
        {
            if (numberOfPulls != 10)
            {
                Logging.WriteLine("[SelectRandomCharacter] Tutorial Gacha Banners must have 10 pulls", LogType.Error);
                throw new ArgumentException("Tutorial Gacha Banners must have 10 pulls");
            }

            // This is the tutorial banner. It gives only one SSR, no more, no less.
            // To make it interesting for the user, select a random spot for the SSR pull
            // We handle it manually because the GachaProbRecord for SSR has a zero probability. It is most likely used by the game for display rather than any processing.
            // Everything else can be handled by the grade probabilities provided by the game files.
            long ssrSpot = random.NextInt64(0, 10);

            for (int i = 0; i < numberOfPulls; i++)
            {
                if (i == ssrSpot)
                {
                    GachaGradeProbRecord gradeProbs = GameData.Instance.GachaGradeProb.Where(p => p.Value.GroupId == gachaType.GradeProbId)
                                                                                                        .Where(p => p.Value.Rare == OriginalRareType.SSR).Select(p => p.Value).First();
                    CharacterRecord character = SelectRandomCharacterFromProbList(gradeProbs, wishlistCharacters, user);
                    selectedCharacters.Add(character);
                }
                else
                {
                    // The gacha type record has a zero probability for SSR, so SSRs won't 
                    CharacterRecord character = SelectRandomCharacter(gachaType, wishlistCharacters, user);
                    selectedCharacters.Add(character);
                }
            }
        }
        else
        {
            // Now using the game probability tables to do the pulls
            for (int i = 0; i < numberOfPulls; i++)
            {
                // Added bannerID to the SelectRandomCharacter method to allow for specific logic such as wishlist handling
                CharacterRecord character = SelectRandomCharacter(gachaType, wishlistCharacters, user);
                selectedCharacters.Add(character);
            }
        }

        return selectedCharacters;
    }


    /// <summary>
    /// Weight that stands for 100% when custom rates are applied.
    /// </summary>
    private const double CustomRateScale = 1_000_000;

    /// <summary>
    /// Re-weights the grade rows so each rarity gets the user's custom share.
    /// Rows inside a rarity keep their split (rate-up vs normal SSR, pilgrim vs other SSR, ...).
    /// A rarity the banner gives no weight to stays at zero.
    /// </summary>
    private static void ApplyCustomRates(GachaRates rates, IEnumerable<GachaGradeProbRecord> grades, Dictionary<int, int> gradeWeights)
    {
        Dictionary<int, int> adjusted = [];
        foreach (var rarity in grades.GroupBy(g => g.Rare))
        {
            int raritySum = rarity.Sum(g => gradeWeights[g.Id]);
            foreach (var g in rarity)
            {
                adjusted[g.Id] = raritySum == 0 ? 0
                    : (int)Math.Round(rates.For(rarity.Key) / 100 * CustomRateScale * gradeWeights[g.Id] / raritySum);
            }
        }

        // Nothing left to roll (e.g. 0% for the only rarity on this banner), keep the official weights
        if (adjusted.Values.Sum() == 0) return;

        foreach (var kv in adjusted)
            gradeWeights[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Rate-up units of the currently active event banners, unordered.
    /// </summary>
    public static HashSet<int> GetActiveRateUpIds(User user) => [.. GetActiveRateUpIdsOrdered(user)];

    /// <summary>
    /// Rate-up units of the currently active event banners, in display order:
    /// newest event first (highest pickup gacha event id), and inside an event pickup units before selectup units.
    /// Chain: banner event -> child events -> shared banner resource table -> pickup gacha event -> GachaType.PickupCharGroupId -> GachaListProb.GroupId
    /// Selectup banners use GachaSelectupListTable (all choices, not the user's pick).
    /// </summary>
    public static List<int> GetActiveRateUpIdsOrdered(User user)
    {
        var data = GameData.Instance;
        var eventManagers = data.eventManagers.Values;
        var perBanner = new List<(int EventKey, List<int> Units)>();

        foreach (var banner in EventHelper.GetLobbyPrivateBannerData(user))
        {
            var resourceTables = eventManagers
                .Where(em => (em.SetField == banner.EventId || em.ParentsEventId == banner.EventId)
                             && em.EventBannerResourceTable != null
                             && em.EventBannerResourceTable.StartsWith("event_"))
                .Select(em => em.EventBannerResourceTable)
                .ToHashSet();

            var gachaEventIds = eventManagers
                .Where(em => em.EventSystemType == EventSystemType.PickupGachaEvent
                             && em.EventBannerResourceTable != null
                             && resourceTables.Contains(em.EventBannerResourceTable))
                .Select(em => em.Id)
                .ToHashSet();

            var units = new List<int>();
            foreach (var gacha in data.gachaTypes.Values
                         .Where(g => gachaEventIds.Contains(g.EventId))
                         .OrderBy(g => g.Type == GachaPremiumType.GachaSelectup ? 1 : 0) // pickup first, then selectup
                         .ThenBy(g => g.Id))
            {
                if (gacha.Type == GachaPremiumType.GachaSelectup)
                {
                    units.AddRange(data.GachaSelectupListTable.Values
                        .Where(s => s.GachaTypeId == gacha.Id)
                        .OrderBy(s => s.Id)
                        .Select(s => s.CharacterId));
                }
                else if (gacha.Type == GachaPremiumType.GachaPickup && gacha.PickupCharGroupId != 0)
                {
                    units.AddRange(data.GachaListProb.Values
                        .Where(l => l.GroupId == gacha.PickupCharGroupId && l.GachaSubType == GachaSubType.PickupCharacter)
                        .OrderBy(l => l.Id)
                        .Select(l => l.GachaId));
                }
            }

            perBanner.Add((gachaEventIds.Count == 0 ? 0 : gachaEventIds.Max(), units));
        }

        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var entry in perBanner.OrderByDescending(b => b.EventKey)) // newest event first
            foreach (var id in entry.Units)
                if (seen.Add(id))
                    result.Add(id);
        return result;
    }

    private static CharacterRecord SelectRandomCharacter(GachaTypeRecord gachaType, List<CharacterRecord> wishlistCharacters, User user)
    {

        // Load the categories and they probabilities
        // This table holds a link to premade lists for each grade and banner. No need to check each character type individually
        Dictionary<int, GachaGradeProbRecord> gradeProbs = GameData.Instance.GachaGradeProb.Where(p => p.Value.GroupId == gachaType.GradeProbId).OrderBy(p => p.Value.Prob).ToDictionary();

        // Build a probability table for the grades
        Dictionary<int, int> gradeWeights = gradeProbs.Values.ToDictionary(g => g.Id, g => g.Prob);

        // Social recruitment uses the official grade/list tables; the tutorial keeps its fixed SSR guarantee.
        if (user.CustomGachaRates != null && gachaType.Type is not (GachaPremiumType.GachaTutorial or GachaPremiumType.GachaFriend))
            ApplyCustomRates(user.CustomGachaRates, gradeProbs.Values, gradeWeights);

        int maxProb = gradeWeights.Values.Sum();

        Dictionary<int, (int minProbInc, int maxProbEx)> gradeProbsTable = new Dictionary<int, (int minProbInc, int maxProbEx)>();

        int curVal = 0;

        foreach (GachaGradeProbRecord gradeProb in gradeProbs.Values)
        {
            gradeProbsTable.Add(gradeProb.Id, new(curVal, curVal + gradeWeights[gradeProb.Id]));
            curVal += gradeWeights[gradeProb.Id];
        }

        // Now do the roll for the grades
        int gradeRoll = (int)random.NextInt64(maxProb);

        GachaGradeProbRecord selectedGrade = gradeProbs[gradeProbsTable.Where(p => gradeRoll >= p.Value.minProbInc && gradeRoll < p.Value.maxProbEx).Select(p => p.Key).First()];

        // Some selectup grades use a character list supplied by the user's choice.
        // Resolve it locally: the shared grade must not retain another user's list.
        int characterListId = selectedGrade.GachaListId;
        if (user.GachaSelectupChoices.TryGetValue(gachaType.Id, out int choiceId)
            && GameData.Instance.GachaSelectupListTable.TryGetValue(choiceId, out var choice)
            && choice.GachaTypeId == gachaType.Id && choice.GachaGradeProbId == selectedGrade.Id
            && choice.GachaListId > 0)
            characterListId = choice.GachaListId;

        // We have the grade, we need to pull the list of characters from GachaListProbTable and create a similar probability table
        // Prefered characters from special banner should be handled automatically by these lists
        // The maximum random value will change depending on how many characters are in the list so we need to keep track of it
        // selectedGrade.GachaListId != selectedGrade.CustomizeListId seem to indicate that wishlisting is supported by the banner/grade. CustomizeListId 19995 and 19996 are most likely SSR and Pilgrims wishlists 

        Dictionary<int, GachaListProbRecord> charProbs = new();

        // Process the wishlist here
        // We filter the character from the category by the ones in the wishlist.
        // CustomizeListId seem to indicate that a wishlist can be specified. It seem to be only different for the two SSR groups in standard banners.
        // Wishlist must not be empty and must have all slots filled in.
        if (wishlistCharacters != null && wishlistCharacters.Count == 20 && characterListId != selectedGrade.CustomizeListId && selectedGrade.CustomizeListId != 0)
        {
            Logging.WriteLine($"Using wishlisted characters.");
            int[] ids = wishlistCharacters.Select(c => c.Id).ToArray();
            charProbs = GameData.Instance.GachaListProb.Where(g => g.Value.GroupId == characterListId).Where(g => ids.Contains(g.Value.GachaId)).ToDictionary();
        }
        // Otherwise, proceed with regular gacha list
        else
        {
            Logging.WriteLine($"Not using wishlisted characters.{(wishlistCharacters == null || wishlistCharacters.Count != 20 ? " Invalid wishlist." : "")}{(characterListId == selectedGrade.CustomizeListId || selectedGrade.CustomizeListId == 0 ? " Invalid Banner or Grade." : "")}");
            charProbs = GameData.Instance.GachaListProb.Where(g => g.Value.GroupId == characterListId).ToDictionary();
        }

        int maxCharProb = 0;

        Dictionary<int, (int minProbInc, int maxProbEx)> charProbsTable = new Dictionary<int, (int minProbInc, int maxProbEx)>();

        foreach (GachaListProbRecord charProb in charProbs.Values)
        {
            charProbsTable.Add(charProb.Id, new(maxCharProb, maxCharProb + charProb.Prob));
            maxCharProb += charProb.Prob;
        }

        // Now, do the pull
        int charRoll = (int)random.NextInt64(maxCharProb);

        GachaListProbRecord selectedCharacter = charProbs[charProbsTable.Where(p => charRoll >= p.Value.minProbInc && charRoll < p.Value.maxProbEx).Select(p => p.Key).First()];


        // We need to check if this is a selectup gacha. The Gacha ID will be empty in this case and must be obtained from the user object.
        int characterID = -1;

        switch (selectedCharacter.GachaType)
        {
            case GachaCategory.GachaSelectup:
                try
                {
                    characterID = GameData.Instance.GachaSelectupListTable[user.GachaSelectupChoices[gachaType.Id]].CharacterId;
                }
                catch
                {
                    Logging.WriteLine("[SelectRandomCharacter] Could not get the character from the selectup choice");
                }
                break;

            default:
                characterID = selectedCharacter.GachaId;
                break;
        }

        // Return
        return GameData.Instance.CharacterTable[characterID]; // GachaId is the character ID

    }

    /// <summary>
    /// This functions is mostly used for the tutorial pull and will not handle the selectup banners (already handled by the main gacha pull function)
    /// </summary>
    /// <param name="selectedGrade"></param>
    /// <param name="wishlistCharacters">Wishlist is handled just in cas but will most likely always be empty</param>
    /// <param name="user"></param>
    /// <returns></returns>
    private static CharacterRecord SelectRandomCharacterFromProbList(GachaGradeProbRecord selectedGrade, List<CharacterRecord> wishlistCharacters, User user)
    {
        Dictionary<int, GachaListProbRecord> charProbs = new();

        // Process the wishlist here
        // We filter the character from the category by the ones in the wishlist.
        // CustomizeListId seem to indicate that a wishlist can be specified. It seem to be only different for the two SSR groups in standard banners.
        // Wishlist must not be empty and must have all slots filled in.
        if (wishlistCharacters != null && wishlistCharacters.Count == 20 && selectedGrade.GachaListId != selectedGrade.CustomizeListId && selectedGrade.CustomizeListId != 0)
        {
            Logging.WriteLine($"Using wishlisted characters.");
            int[] ids = wishlistCharacters.Select(c => c.Id).ToArray();
            charProbs = GameData.Instance.GachaListProb.Where(g => g.Value.GroupId == selectedGrade.GachaListId).Where(g => ids.Contains(g.Value.GachaId)).ToDictionary();
        }
        // Otherwise, proceed with regular gacha list
        else
        {
            Logging.WriteLine($"Not using wishlisted characters.{(wishlistCharacters == null || wishlistCharacters.Count != 20 ? " Invalid wishlist." : "")}{(selectedGrade.GachaListId == selectedGrade.CustomizeListId || selectedGrade.CustomizeListId == 0 ? " Invalid Banner or Grade." : "")}");
            charProbs = GameData.Instance.GachaListProb.Where(g => g.Value.GroupId == selectedGrade.GachaListId).ToDictionary();
        }

        int maxCharProb = 0;

        Dictionary<int, (int minProbInc, int maxProbEx)> charProbsTable = new Dictionary<int, (int minProbInc, int maxProbEx)>();

        foreach (GachaListProbRecord charProb in charProbs.Values)
        {
            charProbsTable.Add(charProb.Id, new(maxCharProb, maxCharProb + charProb.Prob));
            maxCharProb += charProb.Prob;
        }

        // Now, do the pull
        int charRoll = (int)random.NextInt64(maxCharProb);

        GachaListProbRecord selectedCharacter = charProbs[charProbsTable.Where(p => charRoll >= p.Value.minProbInc && charRoll < p.Value.maxProbEx).Select(p => p.Key).First()];

        // Return
        return GameData.Instance.CharacterTable[selectedCharacter.GachaId];
    }

}
