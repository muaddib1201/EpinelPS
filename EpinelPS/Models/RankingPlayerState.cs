namespace EpinelPS.Models;

public sealed class RankingPlayerState
{
    public Dictionary<int, RankingAchievement> Achievements { get; set; } = [];
    public Dictionary<int, long> LegacyCounts { get; set; } = [];
    public HashSet<int> ClaimedServerTargets { get; set; } = [];
    public HashSet<int> MailedCumulativeRewards { get; set; } = [];
    public long LastTriggerAt { get; set; }
}

public sealed class RankingAchievement
{
    public int Rank { get; set; }
    public int Score { get; set; }
    public long AchievedAt { get; set; }
}
