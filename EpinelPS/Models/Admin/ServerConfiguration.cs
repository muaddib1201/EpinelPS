using EpinelPS.Utils;
using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.Models.Admin;

public class ServerConfiguration
{
    public EffectChangeBands EffectBands { get; set; } = new();

    [BindProperty]
    public LogType LogType { get; set; }
}
