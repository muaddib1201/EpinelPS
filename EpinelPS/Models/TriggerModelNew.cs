using System.ComponentModel.DataAnnotations.Schema;
using EpinelPS.Data;

namespace EpinelPS.Models;
public class TriggerModelNew
{
    public Trigger Type { get; set; }
    public long Id { get; set; }
    public long CreatedAt { get; set; }
    public int ConditionId { get; set; }
    public int Value { get; set; }

    [ForeignKey(nameof(UserId))]
    public GameUser User { get; set; }
    public ulong UserId { get; set; }
    
    public NetTrigger ToNet()
    {
        return new()
        {
            ConditionId = ConditionId,
            // All trigger writers (including durable interception/challenge outboxes)
            // retain UTC+9 wall-clock ticks. The wire protocol uses UTC, like ResetHour.
            // Convert at this boundary so old records and outbox deduplication keys stay intact.
            CreatedAt = CreatedAt == 0 ? 0 : CreatedAt - TimeSpan.TicksPerHour * 9,
            Seq = Id,
            Trigger = (int)Type,
            UserValue = Value
        };
    }
}
