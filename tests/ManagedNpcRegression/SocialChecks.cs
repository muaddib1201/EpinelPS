using System.Reflection;
using EpinelPS.Database;
using EpinelPS.Models;
using EpinelPS.Services;
using Newtonsoft.Json;

internal static class SocialChecks
{
    internal static int Run(string root)
    {
        var migration = typeof(ManagedNpcService).GetMethod("UpgradeSocial");
        Check(migration != null, "managed social expansion migration is available");
        string fixture = Path.Combine(root, "Server and Server selector", "db.json");
        var core = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(fixture))!;
        var jsonType = typeof(GameContext).Assembly.GetType("EpinelPS.Database.JsonDb")!;
        var instance = jsonType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
        instance.SetValue(null, core);
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var memberships = core.Users.ToDictionary(u => u.ID, u => JsonConvert.SerializeObject(u.Guild));
        var inventories = core.Users.ToDictionary(u => u.ID, u => JsonConvert.SerializeObject(u.Characters));
        var stages = core.Users.ToDictionary(u => u.ID, u => u.LastNormalStageCleared);
        var tiers = core.Users.Where(u => u.ManagedNpc != null).ToDictionary(u => u.ID, u => u.ManagedNpc!.RosterTier);
        migration!.Invoke(null, [now]);
        core = (CoreInfo)instance.GetValue(null)!;
        Check(core.Guilds.Values.Count(g => g.ManagedNpcGuild) == 5, "three managed unions expand to five");
        Check(core.Guilds.Values.Any(g => g.Data.Name == "OverClock") && core.Guilds.Values.Any(g => g.Data.Name == "貓咪補給站"), "new union names have distinct styles");
        Check(core.Guilds[1].Data.Name == "KUROINU" && core.Users.Count(u => u.Guild.guildId == 1) == 20 && core.Users.Single(u => u.ID == 1552239268).Guild.MemberType == 1, "KUROINU roster and human master retained");
        foreach (var u in core.Users)
        {
            if (JsonConvert.DeserializeObject<GuildData>(memberships[u.ID])!.guildId != null)
                Check(JsonConvert.SerializeObject(u.Guild) == memberships[u.ID], "existing guild membership retained");
            Check(JsonConvert.SerializeObject(u.Characters) == inventories[u.ID] && u.LastNormalStageCleared == stages[u.ID], "inventory and campaign progress retained");
            if (u.ManagedNpc is {} n)
                Check(n.Tier == 2 && n.RosterTier == tiers[u.ID], "all NPCs actively develop with existing strength tier");
        }
        foreach (var guild in core.Guilds.Values.Where(g => g.ManagedNpcGuild && g.Data.Gsn > 4))
        {
            var members = core.Users.Where(u => u.Guild.guildId == guild.Data.Gsn).ToArray();
            Check(members.Length == 8 && members.Count(u => u.Guild.MemberType == 1) == 1, "new unions have eight members and one founder");
        }
        Check(core.Users.Where(u => u.ManagedNpc != null).Select(u => u.ManagedNpc!.RosterTier).Distinct().Count() == 5, "five strength tiers remain represented");
        string beforeRepeat = JsonConvert.SerializeObject(core);
        migration.Invoke(null, [now.AddMinutes(1)]);
        Check(JsonConvert.SerializeObject(instance.GetValue(null)) == beforeRepeat, "social reconciliation is idempotent");
        Console.WriteLine("PASS: five managed unions, distinct names, preserved memberships/inventories/progress, all active across five tiers and repeat migration");
        return 0;
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception("FAIL: " + message);
    }
}
