namespace EpinelPS.Utils;

// Reward helpers normally save and publish triggers immediately. A mail claim needs
// their JSON changes and trigger outbox committed together before those side effects.
internal sealed class RewardMutationScope : IDisposable
{
    [ThreadStatic] private static RewardMutationScope? current;
    private readonly RewardMutationScope? previous;
    private readonly User owner;
    private readonly DateTime? at;
    internal List<TriggerModelNew> Triggers { get; } = [];
    internal static bool DefersSave => current != null;
    internal static DateTime UtcNow => current?.at ?? DateTime.UtcNow;
    internal static IEnumerable<TriggerModelNew> Pending(User user) =>
        current != null && ReferenceEquals(current.owner, user) ? current.Triggers : [];

    internal RewardMutationScope(User user, DateTime? at = null)
    {
        owner = user; this.at = at ?? current?.at; previous = current; current = this;
    }
    internal static bool TryCapture(User user, EpinelPS.Data.Trigger type, int value, int condition, out TriggerModelNew trigger)
    {
        trigger = null!;
        if (current == null || !ReferenceEquals(current.owner, user)) return false;
        long at = Math.Max(UtcNow.AddHours(9).Ticks, user.LastMailRewardTriggerAt + 1);
        user.LastMailRewardTriggerAt = at;
        trigger = new TriggerModelNew { UserId = user.ID, Type = type, Value = value, ConditionId = condition, CreatedAt = at };
        current.Triggers.Add(trigger);
        return true;
    }
    public void Dispose() => current = previous;
}
