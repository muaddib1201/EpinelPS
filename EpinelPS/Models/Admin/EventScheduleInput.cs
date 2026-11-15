namespace EpinelPS.Models.Admin;

public sealed class EventScheduleInput
{
    public int BannerId { get; set; }
    public string Operation { get; set; } = "";
    public int? Days { get; set; }
    public string? EndBeijing { get; set; }
}
