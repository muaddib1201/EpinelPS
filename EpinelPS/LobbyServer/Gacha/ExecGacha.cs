//todo
//implement response.Reward 
// and response.Currencies
//NetUserCurrencyData fields Type 9000 and Value 150
//NetRewardData field Currency = new NetUserCurrencyData copy type and value from response.Currencies new NetUserCurrencyData


// Gacha backend data structure:
//
//  GachaTypeTable (Id = tid passed when called ExecGacha) - Identifies the banner calling the gacha process (standard, social, specific character, etc.)
//      GachaGradeProbTable (GroupId = GachaTypeTable.GradeProbId) - Lists 4 groups with probabilities (R, SR, SSR, Pilgrim/Overspecs)
//          GachaListProbTable (GroupId = GachaGradeProbTable.GachaListId) - Lists individual character per group
//



using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Gacha;

[GameRequest("/gacha/execute")]
public class ExecGacha : LobbyMessage
{
    private static readonly Random random = new();

    // Some magic numbers for convenience
    private const int STANDARD_BANNER_ID = 1;
    private const int SOCIAL_BANNER_ID = 2;
    private const int NEW_PLAYER_SPECIAL_BANNER_ID = 4;

    // Exclusion lists for sick pulls mode and normal mode 2500601 is the broken R rarity dorothy
    private static readonly List<int> sickPullsExclusionList = [2500601]; // Add more IDs as needed
    //private static readonly List<int> normalPullsExclusionList = [2500601, 422401, 306201, 399901, 399902, 399903, 399904, 201401, 301501, 112101, 313201, 319301, 319401, 320301, 422601, 426101, 328301, 328401, 235101, 235301, 136101, 339201, 140001, 140101, 140201, 580001, 580101, 580201, 581001, 581101, 581201, 582001, 582101, 582201, 583001, 583101, 583201, 583301, 190101, 290701]; // Add more IDs as needed

    protected override async Task HandleAsync()
    {
        // Get the request data
        ReqExecuteGacha req = await ReadData<ReqExecuteGacha>();
        var user = GetUser();
        ResExecuteGacha response;
        response = MissionProgressDelivery.Apply(user, () => Execute(user, req));
        await WriteDataAsync(response);
    }

    internal static ResExecuteGacha Execute(User user, ReqExecuteGacha req)
    {
        if (req.Count is not (1 or 10)) throw new BadHttpRequestException("Invalid recruit quantity.", 400);
        // Count determines whether we select 1 or 10 characters
        int numberOfPulls = req.Count == 1 ? 1 : 10;

        //Get the banner ID and load the banner data from GachaTypeTable
        int bannerID = req.Tid;
        Logging.WriteLine($"Banner ID: {bannerID}");
        GachaTypeRecord gachaType = GameData.Instance.gachaTypes[bannerID];
        Logging.WriteLine($"Banner Type: {gachaType.Type}");

        ResExecuteGacha response = new() { Reward = new NetRewardData() { PassPoint = new() } };


        Logging.WriteLine($"Currency type: {(CurrencyType)req.CurrencyType}");
        
        List<CharacterRecord> selectedCharacters = GachaUtils.ExecuteGachaPull(gachaType, numberOfPulls, user);
        bool obtainedNewCharacter = false;

        int totalBodyLabels = 0;

        foreach (CharacterRecord characterData in selectedCharacters)
        {
            obtainedNewCharacter |= !user.HasCharacter(characterData.Id);
            var reward = CharacterRewardHelper.GrantCharacter(user, characterData, 1);
            var result = reward.Character.Single();
            response.Characters.AddRange(reward.UserCharacters);
            response.Items.AddRange(reward.UserItems);
            response.Reward.MergeFrom(reward);
            response.Gacha.Add(new NetGachaEntityData
            {
                Sn = result.Csn, Tid = characterData.Id, Type = 1,
                PieceCount = result.PieceCount, CurrencyValue = result.CurrencyValue,
            });
            totalBodyLabels += checked((int)result.CurrencyValue);
            user.AddTrigger(Trigger.GachaCharacter, 1);
        }

        // A newly obtained character may satisfy a room unlock condition while
        // the conversation's trigger list was already satisfied. Reconcile
        // eligible openers after the complete pull, once all character data and
        // acquisition triggers have been written. This does not backfill
        // triggers and does not run from /messenger/get.
        if (obtainedNewCharacter)
            MessengerMessageCreator.CreateAllEligibleOpeners(user);

        CurrencyType ticketType = (CurrencyType)req.CurrencyType;

        // ==========================
        // CAPABILITIES (single source of truth)
        // ==========================
        bool canUsePremiumTicket = ticketType == CurrencyType.CharPremiumTicket;
        bool canUseCustomizeTicket = ticketType == CurrencyType.CharCustomizeTicket;
        bool canUseFreeCash =
            ticketType == CurrencyType.CharPremiumTicket ||
            ticketType == CurrencyType.CharCustomizeTicket ||
            ticketType == CurrencyType.FreeCash;

        // ==========================
        // STATE
        // ==========================
        long pullsLeft = numberOfPulls;
        bool discount = req.IsDiscount;

        long usePremiumTickets = 0;
        long useCharCustomizeTickets = 0;
        long useFreeCash = 0;
        long useChargeCash = 0;
        long useFriendshipPoint = 0;

        long userPremiumTickets = user.GetCurrencyVal(CurrencyType.CharPremiumTicket);
        long userCharCustomizeTickets = user.GetCurrencyVal(CurrencyType.CharCustomizeTicket);
        long userFreeCash = user.GetCurrencyVal(CurrencyType.FreeCash);
        // ==========================
        // EXCLUSIVE CURRENCIES
        // ==========================
        switch (ticketType)
        {
            case CurrencyType.ChargeCash:
                {
                    var cashPrice = gachaType.GachaPriceGroup.Where(g => g.GachaPriceType == 98 /*CurrencyType.ChargeCash*/ || g.GachaPriceType == 99 /*CurrencyType.FreeCash*/).First();
                    useChargeCash = pullsLeft * (discount ? cashPrice.DailyGachaDiscountPriceValue1 : cashPrice.GachaPriceValueCount1);
                    pullsLeft = 0;
                }
                break;

            case CurrencyType.FriendshipPoint:
                {
                    var fpPrice = gachaType.GachaPriceGroup.Where(g => g.GachaPriceType == 4000 /*CurrencyType.FriendshipPoint*/).First();
                    useFriendshipPoint = pullsLeft * fpPrice.GachaPriceValueCount1;
                    pullsLeft = 0;
                }
                break;
        }

        // ==========================
        // MIXED PAYMENT PIPELINE
        // ==========================
        if (canUsePremiumTicket)
        {
            usePremiumTickets = Math.Min(userPremiumTickets, pullsLeft);
            pullsLeft -= usePremiumTickets;
        }

        if (canUseCustomizeTicket)
        {
            useCharCustomizeTickets = Math.Min(userCharCustomizeTickets, pullsLeft);
            pullsLeft -= useCharCustomizeTickets;
        }

        if (canUseFreeCash && gachaType.Type != GachaPremiumType.GachaTutorial)
        {
            var cashPrice = gachaType.GachaPriceGroup.Where(g => g.GachaPriceType == 98 /*CurrencyType.ChargeCash*/ || g.GachaPriceType == 99 /*CurrencyType.FreeCash*/).First();
            long costPerPull = discount ? cashPrice.DailyGachaDiscountPriceValue1 : cashPrice.GachaPriceValueCount1;
            long totalCostNeeded = pullsLeft * costPerPull;

            useFreeCash = Math.Min(userFreeCash, totalCostNeeded);
            useChargeCash = totalCostNeeded - useFreeCash;

            pullsLeft = 0;
        }

        // ==========================
        // APPLY CURRENCY CHANGES
        // ==========================
        void ApplyCurrency(CurrencyType type, long delta)
        {
            if (delta == 0) return;

            if (delta < 0)
            {
                if (!user.SubtractCurrency(type, -delta))
                    throw new BadHttpRequestException("Insufficient currency to recruit.", 400);
            }
            else
                user.AddCurrency(type, delta);

            response.Currencies.Add(new NetUserCurrencyData
            {
                Type = (int)type,
                Value = user.GetCurrencyVal(type)
            });
        }

        ApplyCurrency(CurrencyType.CharPremiumTicket, -usePremiumTickets);
        ApplyCurrency(CurrencyType.CharCustomizeTicket, -useCharCustomizeTickets);
        ApplyCurrency(CurrencyType.FreeCash, -useFreeCash);
        ApplyCurrency(CurrencyType.ChargeCash, -useChargeCash);
        ApplyCurrency(CurrencyType.FriendshipPoint, -useFriendshipPoint);
        if (pullsLeft > 0 && gachaType.Type != GachaPremiumType.GachaTutorial)
            throw new BadHttpRequestException("Unsupported recruit payment currency.", 400);
        // CharacterRewardHelper already credited this currency exactly once.
        if (totalBodyLabels > 0)
            response.Currencies.Add(new NetUserCurrencyData
            {
                Type = (int)CurrencyType.DissolutionPoint,
                Value = user.GetCurrencyVal(CurrencyType.DissolutionPoint),
            });

        // ==========================
        // RECORD DISCOUNT USAGE
        // ==========================
        if (discount)
        {
            user.DailyDiscountUsed = true;
        }

        // ==========================
        // MILEAGE REWARDS
        // ==========================
        if (bannerID == STANDARD_BANNER_ID)
            ApplyCurrency(CurrencyType.SilverMileageTicket, numberOfPulls);

        if (bannerID != STANDARD_BANNER_ID && bannerID != SOCIAL_BANNER_ID && bannerID != NEW_PLAYER_SPECIAL_BANNER_ID && gachaType.Type != GachaPremiumType.GachaTutorial) // TODO: Handle daily free pulls. They should not give Gold Mileage.
            ApplyCurrency(CurrencyType.GoldMileageTicket, numberOfPulls);

         
        user.AddGachaPullCount(bannerID, numberOfPulls);

        // ==========================
        // BANNER PAYBACK RECORDS
        // ==========================

        // Does the banner have a payback list?
        var prs = GameData.Instance.GachaPaybackRecords.Where(p => p.Value.GachaId == bannerID).ToDictionary();

        if (prs.Count > 0)
        {

            var pr = prs.First();

            Dictionary<int, GachaPaybackStepRecord_Raw> steps = GameData.Instance.GachaPaybackStepRecords.Where(s => s.Value.PaybackId == pr.Value.Id).ToDictionary();

            //Process the banner pity
            GachaPaybackData paybackState;

            if (user.GachaPaybackData.ContainsKey(pr.Value.GachaId))
            {
                paybackState = user.GachaPaybackData[pr.Value.GachaId];
            }
            else
            {
                paybackState = new GachaPaybackData()
                {
                    GachaId = pr.Value.GachaId,    //payback GachaId
                    GachaCount = 0                 // Pull amount
                };
                user.GachaPaybackData.Add(pr.Value.GachaId, paybackState);
            }

            paybackState.GachaCount += req.Count;
        }

        JsonDb.Save();

        return response;
    }

}
