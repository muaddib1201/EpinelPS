using EpinelPS.Utils;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.Models.Admin;

public class ServerConfiguration
{
    public EffectChangeBands EffectBands { get; set; } = new();

    public decimal[] ValueResetRates { get; set; } = ValueResetTiers.DefaultRates();

    [BindProperty]
    public LogType LogType { get; set; }
}
