using EpinelPS.Controllers;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Commands.Core;
using EpinelPS.Commands.Binding;
using EpinelPS.Utils;

namespace EpinelPS.Commands.Handler;

public class GachaRatesParameter : ICommandParameters
{
    static ParameterDescriptor[] ICommandParameters.Descriptors => [
        Param.Float(0, "ssr", "SSR rate in percent").WithConstraint(new FloatRangeConstraint(0, 100)),
        Param.Float(1, "sr", "SR rate in percent").WithConstraint(new FloatRangeConstraint(0, 100)),
        Param.Float(2, "r", "R rate in percent").WithConstraint(new FloatRangeConstraint(0, 100)),
    ];

    public float Ssr { get; init; }
    public float Sr { get; init; }
    public float R { get; init; }
}

public class GachaRatesHandler(IExecutionContext context) : BaseHandler<GachaRatesParameter>(context)
{
    public override string Name => "gacha-rates";
    public override string Description => "Set custom SSR/SR/R gacha rates (percent, must add up to 100), excluding tutorial and social recruitment";

    protected async override Task<HandleResult> ExecuteAsync(GachaRatesParameter parameters)
    {
        if (context.SelectedUser == null)
            return new HandleResult(false, "No user selected");

        // float -> double without the float noise (0.1f would otherwise show as 0.100000001)
        double ssr = Math.Round(parameters.Ssr, 4), sr = Math.Round(parameters.Sr, 4), r = Math.Round(parameters.R, 4);

        string? error = GachaRates.Validate(ssr, sr, r);
        if (error != null)
            return new HandleResult(false, error);

        context.SelectedUser.CustomGachaRates = new GachaRates { Ssr = ssr, Sr = sr, R = r };
        JsonDb.Save();
        return new HandleResult(true, $"Gacha rates set to {context.SelectedUser.CustomGachaRates}");
    }
}

public class GachaRatesResetParameter : ICommandParameters
{
    static ParameterDescriptor[] ICommandParameters.Descriptors => [];
}

public class GachaRatesResetHandler(IExecutionContext context) : BaseHandler<GachaRatesResetParameter>(context)
{
    public override string Name => "gacha-rates-reset";
    public override string Description => "Go back to the official gacha rates for the selected user";

    protected async override Task<HandleResult> ExecuteAsync(GachaRatesResetParameter parameters)
    {
        if (context.SelectedUser == null)
            return new HandleResult(false, "No user selected");

        context.SelectedUser.CustomGachaRates = null;
        JsonDb.Save();
        return new HandleResult(true, "Gacha rates reset to official");
    }
}

public class GachaSimParameter : ICommandParameters
{
    static ParameterDescriptor[] ICommandParameters.Descriptors => [
        Param.Int(0, "gachaTypeId", "Gacha type (banner) ID, e.g. 5 for the standard banner"),
        Param.Int(1, "pulls", "Number of pulls to simulate").WithConstraint(new IntRangeConstraint(1, 1_000_000)),
    ];

    public int GachaTypeId { get; init; }
    public int Pulls { get; init; }
}

public class GachaSimHandler(IExecutionContext context) : BaseHandler<GachaSimParameter>(context)
{
    public override string Name => "gacha-sim";
    public override string Description => "Simulate pulls on a banner for the selected user and print the rarity split (nothing is granted)";

    protected async override Task<HandleResult> ExecuteAsync(GachaSimParameter parameters)
    {
        if (context.SelectedUser == null)
            return new HandleResult(false, "No user selected");

        if (!GameData.Instance.gachaTypes.TryGetValue(parameters.GachaTypeId, out var gachaType))
        {
            string known = string.Join(", ", GameData.Instance.gachaTypes.Values.Take(30).Select(g => $"{g.Id}={g.Type}"));
            return new HandleResult(false, $"Unknown gacha type {parameters.GachaTypeId}. Some known IDs: {known}");
        }

        if (gachaType.Type == GachaPremiumType.GachaTutorial)
            return new HandleResult(false, "The tutorial banner always gives exactly one SSR per 10 pulls");

        var pulled = GachaUtils.ExecuteGachaPull(gachaType, parameters.Pulls, context.SelectedUser);

        var byRarity = pulled.GroupBy(c => c.OriginalRare).ToDictionary(g => g.Key, g => g.Count());
        string split = string.Join(" / ", new[] { OriginalRareType.SSR, OriginalRareType.SR, OriginalRareType.R }
            .Select(r => $"{r} {100.0 * byRarity.GetValueOrDefault(r) / pulled.Count:0.00}%"));

        string topSsr = string.Join(", ", pulled.Where(c => c.OriginalRare == OriginalRareType.SSR)
            .GroupBy(c => c.Id)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"{AdminApiController.LookupRealName(g.First().NameLocalkey ?? "") ?? g.Key.ToString()} x{g.Count()}"));

        string rates = gachaType.Type == GachaPremiumType.GachaFriend
            ? "official (social recruit)" : context.SelectedUser.CustomGachaRates?.ToString() ?? "official";
        return new HandleResult(true, $"Banner {gachaType.Id} ({gachaType.Type}), {pulled.Count} pulls, rates: {rates}\n{split}\nTop SSR: {topSsr}");
    }
}
