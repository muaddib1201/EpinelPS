namespace EpinelPS.Models;

public class ManagedNpcState
{
    public string Name { get; set; } = "";
    public int Seed { get; set; }
    public int Tier { get; set; }
    public int RosterTier { get; set; }
    public int TeamSchemaVersion { get; set; }
    public int GrowthSchemaVersion { get; set; }
    public long RecruitSequence { get; set; }
    public int BurstCycleSeconds { get; set; }
    public List<string> TeamSynergies { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public int ActiveStartHourUtc { get; set; }
    public int ActiveHours { get; set; } = 10;
    public DateTime CreatedUtc { get; set; }
    public DateTime NextActionUtc { get; set; }
    public DateTime LastActionUtc { get; set; }
    public DateTime NextRecruitUtc { get; set; }
    public DateTime NextSkillUtc { get; set; }
    public long Sequence { get; set; }
    public int Day { get; set; }
    public int StagesToday { get; set; }
    public int LevelsToday { get; set; }
    public int ActivityToday { get; set; }
    public int ShootingToday { get; set; }
    public long TotalStages { get; set; }
    public long TotalActions { get; set; }
    public List<NpcActionRecord> Actions { get; set; } = [];
}

public class NpcActionRecord
{
    public DateTime AtUtc { get; set; }
    public string Text { get; set; } = "";
}

public class NpcManagementSettings
{
    public int SocialSchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;
    public double Speed { get; set; } = 1;
    public DateTime LastProcessedUtc { get; set; }
    public string? PendingRequestId { get; set; }
    public Dictionary<string, int> CreatedBatches { get; set; } = [];
    public List<User> PendingUsers { get; set; } = [];
    public Dictionary<int, SavedGuild> PendingGuilds { get; set; } = [];
}
