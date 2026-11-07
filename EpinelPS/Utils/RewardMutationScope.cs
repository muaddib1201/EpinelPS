namespace EpinelPS.Utils;

// Reward helpers normally save and publish triggers immediately. A mail claim needs
// their JSON changes and trigger outbox committed together before those side effects.
internal sealed class RewardMutationScope : IDisposable
{
    [ThreadStatic] private static RewardMutationScope? current;
    private readonly RewardMutationScope? previous;
    private readonly User owner;
    internal List<TriggerModelNew> Triggers { get; } = [];
    internal static bool DefersSave => current != null;

    internal RewardMutationScope(User user)
    {
        owner = user; previous = current; current = this;
    }
    internal static bool TryCapture(User user, EpinelPS.Data.Trigger type, int value, int condition, out TriggerModelNew trigger)
    {
        trigger = null!;
        if (current == null || !ReferenceEquals(current.owner, user)) return false;
        long at = Math.Max(DateTime.UtcNow.AddHours(9).Ticks, user.LastMailRewardTriggerAt + 1);
        user.LastMailRewardTriggerAt = at;
        trigger = new TriggerModelNew { UserId = user.ID, Type = type, Value = value, ConditionId = condition, CreatedAt = at };
        current.Triggers.Add(trigger);
        return true;
    }
    public void Dispose() => current = previous;
}
