using System.Reflection;
using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class MigrationPreview
{
    internal static int Run(string root)
    {
        var live = Path.Combine(root, "Server and Server selector");
        var output = Path.Combine(AppContext.BaseDirectory, "preview.db");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "db.json"), File.ReadAllText(Path.Combine(live, "db.json")));
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(live, "epinelps.db"), Mode = SqliteOpenMode.ReadOnly }.ToString()))
        using (var target = new SqliteConnection($"Data Source={output}"))
        {
            source.Open(); target.Open(); source.BackupDatabase(target);
            using var original = new SqliteConnection($"Data Source={Path.Combine(AppContext.BaseDirectory, "preview-before.db")}");
            original.Open(); target.BackupDatabase(original);
        }
        GameContext.InitializeOptions($"Data Source={output}", "sqlite");
        var jsonDb = typeof(User).Assembly.GetType("EpinelPS.Database.JsonDb")!;
        CoreInfo State() => (CoreInfo)jsonDb.GetProperty("Instance")!.GetValue(null)!;
        var before = JsonConvert.DeserializeObject<CoreInfo>(JsonConvert.SerializeObject(State()))!;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview-before.json"), JsonConvert.SerializeObject(before));
        var now = DateTime.UtcNow;
        Invoke("EpinelPS.LobbyServer.Guild.GuildActivity", "RecoverAll", now);
        Invoke("EpinelPS.LobbyServer.Mission.MissionProgressDelivery", "RecoverAll", now);
        string settled = JsonConvert.SerializeObject(State());
        Invoke("EpinelPS.LobbyServer.Guild.GuildActivity", "RecoverAll", now);
        Invoke("EpinelPS.LobbyServer.Mission.MissionProgressDelivery", "RecoverAll", now);
        if (settled != JsonConvert.SerializeObject(State())) throw new Exception("Repeating the repair changed the settled save.");
        foreach (var original in before.Users)
        {
            var current = State().Users.Single(u => u.ID == original.ID);
            var a = JObject.FromObject(original); var b = JObject.FromObject(current);
            foreach (var node in new[] { a, b })
            {
                node.Remove("ShopMissionHistoryRepaired"); node.Remove("LastMailRewardTriggerAt"); node.Remove("PendingMissionTriggers");
                node.Remove("GuildContributionByDay");
                ((JObject)node["Guild"]!).Remove("ActivityByDay");
            }
            if (!JToken.DeepEquals(a, b)) throw new Exception($"Repair changed unrelated player state for {original.ID}.");
        }
        using var db = GameContext.CreateNew();
        foreach (var human in State().Users.Where(u => u.ManagedNpc == null))
        {
            var original = before.Guilds[human.Guild.guildId!.Value];
            var guild = State().Guilds[human.Guild.guildId.Value];
            int reset = State().ResetHourUtcTime;
            long start = now.AddHours(-reset).Date.AddHours(reset + 9).Ticks;
            int shopCount = db.Triggers.Where(t => t.UserId == human.ID && t.Type == Trigger.MainShopBuy && t.CreatedAt >= start).Sum(t => t.Value);
            Console.WriteLine($"PASS real-save copy repair: human={human.ID}, claimed daily={human.ResetableData.DailyMissionPoints}, Union activity added={guild.Data.ActivityPoint - original.Data.ActivityPoint}, exp={guild.Data.Exp}, ordinary-shop progress={shopCount}; inventories, wallets, claims, NPC teams and profiles preserved; repeated repair unchanged");
        }
        return 0;
    }

    private static void Invoke(string type, string method, DateTime now) => typeof(User).Assembly.GetType(type)!
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [now]);
}
