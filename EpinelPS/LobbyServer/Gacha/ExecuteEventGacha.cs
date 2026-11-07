//yes i am lazy and its preety much same as exec gacha 
//but only does 1x pull
//its here only so there is no system error on 1x free gacha event

using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Utils;

namespace EpinelPS.LobbyServer.Gacha;

[GameRequest("/gacha/event/execute")]
public class ExecuteEventGacha : LobbyMessage
{
    private static readonly Random random = new();

    // Exclusion lists for sick pulls mode and normal mode 2500601 is the broken R rarity dorothy
    private static readonly List<int> sickPullsExclusionList = [2500601]; // Add more IDs as needed
    private static readonly List<int> normalPullsExclusionList = [2500601, 422401, 306201, 399901, 399902, 399903, 399904, 201401, 301501, 112101, 313201, 319301, 319401, 320301, 422601, 426101, 328301, 328401, 235101, 235301, 136101, 339201, 140001, 140101, 140201, 580001, 580101, 580201, 581001, 581101, 581201, 582001, 582101, 582201, 583001, 583101, 583201, 583301, 190101, 290701]; // Add more IDs as needed

    protected override async Task HandleAsync()
    {
        ReqExecuteDailyFreeGacha req = await ReadData<ReqExecuteDailyFreeGacha>();

        User user = GetUser();
        var response = MissionProgressDelivery.Apply(user, () => Execute(user, req));
        await WriteDataAsync(response);
    }

    private static ResExecuteDailyFreeGacha Execute(User user, ReqExecuteDailyFreeGacha req)
    {
        int bannerID = req.GachaId;
        GachaTypeRecord gachaType = GameData.Instance.gachaTypes[bannerID];
        if (gachaType.DailyFreeGachaEventId <= 0 || req.EventId != gachaType.DailyFreeGachaEventId)
            throw new InvalidOperationException("Free recruit event does not match the banner");
        ResExecuteDailyFreeGacha response = new();
        lock (user)
        {
            if (user.GachaDailyFreePulls.Contains(req.EventId))
                throw new InvalidOperationException("Daily free recruit already claimed");
            var characterData = GachaUtils.ExecuteGachaPull(gachaType, 1, user).Single();
            var reward = CharacterRewardHelper.GrantCharacter(user, characterData, 1);
            var result = reward.Character.Single();
            response.Gacha.Add(new NetGachaEntityData
            {
                Sn = result.Csn, Tid = characterData.Id, Type = 1,
                PieceCount = result.PieceCount, CurrencyValue = result.CurrencyValue,
            });
            response.Characters.AddRange(reward.UserCharacters);
            response.Items.AddRange(reward.UserItems);
            foreach (var currency in reward.Currency.GroupBy(c => c.Type))
                response.Currencies.Add(new NetUserCurrencyData { Type = currency.Key, Value = currency.Last().FinalValue });
            user.AddTrigger(Trigger.GachaCharacter, 1);
            user.GachaDailyFreePulls.Add(req.EventId);
            user.AddGachaPullCount(bannerID, 1);
            JsonDb.Save();
        }

        return response;
    }
   
}
