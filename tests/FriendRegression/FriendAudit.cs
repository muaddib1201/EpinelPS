using System.Net;
using System.Text;
using EpinelPS.Data;
using EpinelPS.Models;
using EpinelPS.Services;
using EpinelPS.Utils;
using Newtonsoft.Json;

internal static class FriendAudit
{
    internal static int Run(string source, string output)
    {
        var core = JsonConvert.DeserializeObject<CoreInfo>(File.ReadAllText(source))!;
        var human = core.Users.Single(u => u.ID == 1552239268);
        var npcs = core.Users.Where(u => u.ManagedNpc != null).ToArray();
        var ranked = npcs.Where(u => u.ManagedNpc!.Enabled).Select(u => new
        {
            id = u.ID, name = u.ManagedNpc!.Name, combat = u.RepresentationTeamDataNew.Sum(c => (long)FormulaUtils.CalculateCP(u, c)),
            commanderLevel = u.userPointData.UserLevel,
            mainLevel = u.Characters.Where(c => u.RepresentationTeamDataNew.Contains(c.Csn)).Select(c => ManagedNpcGrowth.Level(u, c)).Min(),
            tier = u.ManagedNpc.RosterTier + 1, tierName = ManagedNpcRoster.TierNames[u.ManagedNpc.RosterTier],
            ssr = ManagedNpcRoster.SsrCount(u), guild = core.Guilds.GetValueOrDefault(u.Guild.guildId ?? 0)?.Data.Name ?? "未入盟"
        }).OrderByDescending(u => u.combat).ThenByDescending(u => u.commanderLevel).ThenBy(u => u.id).ToArray();
        ulong[] selected = ranked.Take(30).Select(u => u.id).ToArray();
        Check(npcs.Length == 100 && npcs.All(u => u.ManagedNpc!.Tier == 2), "all one hundred NPCs actively develop");
        Check(core.Guilds.Values.Count(g => g.ManagedNpcGuild) == 5, "five managed unions exist");
        Check(human.Friendships is { NpcFriendsInitialized: true } && human.Friendships.Friends.Count == 30 && human.Friendships.Friends.SetEquals(selected), "saved friend list matches the thirty strongest at initialization");
        Check(selected.All(id => core.Users.Single(u => u.ID == id).Friendships?.Friends.Contains(human.ID) == true), "all selected relations are mutual");
        var guilds = core.Guilds.OrderBy(g => g.Key).Select(g => new { id = g.Key, name = g.Value.Data.Name, managed = g.Value.ManagedNpcGuild,
            members = core.Users.Count(u => u.Guild.guildId == g.Key) }).ToArray();
        File.WriteAllText(output, JsonConvert.SerializeObject(new { capturedUtc = DateTime.UtcNow, source,
            ownerId = human.ID, selectedIds = selected, guilds, npcCount = npcs.Length, activeNpcCount = npcs.Count(u => u.ManagedNpc!.Enabled),
            sent = human.Friendships!.SentCount, received = human.Friendships.ReceivedCount, socialPoints = human.GetCurrencyVal(CurrencyType.FriendshipPoint), ranking = ranked }, Formatting.Indented));
        string H(object value) => WebUtility.HtmlEncode(value.ToString());
        var html = new StringBuilder("<!doctype html><html lang=zh-CN><meta charset=utf-8><title>NPC 好友与联盟结果</title><style>body{font:16px/1.5 system-ui;background:#f5f7fa;color:#18212e;max-width:1150px;margin:40px auto}table{border-collapse:collapse;background:white;width:100%;margin:20px 0}td,th{padding:10px 14px;text-align:left;border-bottom:1px solid #ddd}th{background:#174f47;color:white}small{color:#64748b}</style><h1>NPC 好友与联盟结果</h1><p>100 名 NPC 统一积极养成，保留起步、初成、进阶、成熟、资深五档练度。以下 30 名好友按初始化时主力战力排序，建立双向好友关系，每日可互赠与领取社交点数。</p><p><small>北京时间每日 04:00 重置；每天最多赠送、领取各 30 次，每次领取 1 点。初始化不消费玩家次数、不直接发放点数。客户端显示与按钮仍需游戏内确认。</small></p><h2>联盟</h2><table><tr><th>ID</th><th>联盟名</th><th>类型</th><th>成员</th></tr>");
        foreach (var g in guilds) html.Append($"<tr><td>{g.id}</td><td>{H(g.name)}</td><td>{(g.managed ? "托管联盟" : "玩家联盟")}</td><td>{g.members}</td></tr>");
        html.Append("</table><h2>最强 30 名 NPC 好友</h2><table><tr><th>#</th><th>指挥官</th><th>UID</th><th>主力战力</th><th>主力等级</th><th>练度档位</th><th>SSR</th><th>联盟</th></tr>");
        int rank = 0;
        foreach (var u in ranked.Take(30)) html.Append($"<tr><td>{++rank}</td><td>{H(u.name)}</td><td>{u.id}</td><td>{u.combat:N0}</td><td>{u.mainLevel}</td><td>第 {u.tier} 档 · {H(u.tierName)}</td><td>{u.ssr}</td><td>{H(u.guild)}</td></tr>");
        html.Append("</table><p><small>主力战力相同时按指挥官等级降序、UID 升序选择。名单初始化一次，之后不会自动替换或补回主动删除的好友。</small></p></html>");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!, "NPC好友与联盟结果.html"), html.ToString());
        Console.WriteLine("PASS: saved migration audit matches all five unions, one hundred active-growth NPCs and the exact strongest thirty mutual friends");
        return 0;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
}
