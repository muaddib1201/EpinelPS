using System.ComponentModel.DataAnnotations;

namespace EpinelPS.Models.Admin;

public class ManagedNpcAdminModel
{
    public NpcManagementSettings Settings { get; set; } = new();
    public List<User> Players { get; set; } = [];
    public Dictionary<int, string> Guilds { get; set; } = [];
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
}
public class NpcCreateForm
{
    [Range(1, 200)] public int Count { get; set; } = 30;
    [Required, StringLength(64)] public string RequestId { get; set; } = "";
}
public class NpcConfigureForm
{
    public bool Enabled { get; set; }
    [Range(.25, 4)] public double Speed { get; set; } = 1;
}
public class NpcUpdateForm
{
    public ulong Id { get; set; }
    public bool Enabled { get; set; }
    [Range(0, 2)] public int Tier { get; set; } = 2;
    public int? GuildId { get; set; }
}
