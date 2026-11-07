using Newtonsoft.Json;

namespace EpinelPS.LobbyServer.SoloraidMuseum;

// These are explicitly labelled, fixed NPC benchmark records. They do not
// represent official players or battles submitted by a local account.
internal static class MuseumHistoryArchive
{
    private sealed class Archive
    {
        public List<List<NetSoloRaidMuseumTeamCharacter>> Lineups { get; set; } = [];
        public List<Stage> Stages { get; set; } = [];
    }

    private sealed class Stage
    {
        public int StageId { get; set; }
        public long CommonHp { get; set; }
        public List<Ranker> Rankers { get; set; } = [];
    }

    private sealed class Ranker
    {
        public long Usn { get; set; }
        public int Ranking { get; set; }
        public string Nickname { get; set; } = "";
        public int Icon { get; set; }
        public long Damage { get; set; }
        public List<Squad> Squads { get; set; } = [];
    }

    private sealed class Squad
    {
        public int TeamNumber { get; set; }
        public int Lineup { get; set; }
        public long Damage { get; set; }
    }

    private static readonly Lazy<Archive> Data = new(Load);
    private static readonly HashSet<int> DisplayRanks = [50, 30, 15, 5, 1];

    private static Archive Load()
    {
        using var stream = typeof(MuseumHistoryArchive).Assembly.GetManifestResourceStream(
            "EpinelPS.LobbyServer.SoloraidMuseum.MuseumNpcHistory.json")
            ?? throw new InvalidDataException("Museum NPC history resource is missing.");
        using var reader = new StreamReader(stream);
        var archive = JsonConvert.DeserializeObject<Archive>(reader.ReadToEnd())
            ?? throw new InvalidDataException("Museum NPC history resource is empty.");
        if (archive.Lineups.Count != 5 || archive.Lineups.Any(t => t.Count != 5) ||
            archive.Stages.Select(s => s.StageId).Distinct().Count() != archive.Stages.Count)
            throw new InvalidDataException("Invalid museum NPC archive structure.");
        var identities = new HashSet<long>();
        foreach (var stage in archive.Stages)
        {
            if (!stage.Rankers.Select(r => r.Ranking).SequenceEqual(Enumerable.Range(1, 50)) ||
                stage.Rankers.Zip(stage.Rankers.Skip(1)).Any(p => p.First.Damage <= p.Second.Damage))
                throw new InvalidDataException("Invalid museum NPC ranking order.");
            foreach (var ranker in stage.Rankers)
                if (ranker.Usn <= 0 || !identities.Add(ranker.Usn) || !ranker.Nickname.StartsWith("NPC", StringComparison.Ordinal) ||
                    ranker.Damage <= 0 || !ranker.Squads.Select(s => s.TeamNumber).SequenceEqual(Enumerable.Range(1, 5)) ||
                    ranker.Squads.Any(s => s.Damage <= 0 || s.Lineup < 0 || s.Lineup >= archive.Lineups.Count) ||
                    ranker.Squads.Select(s => s.Lineup).Distinct().Count() != 5 ||
                    ranker.Squads.Sum(s => s.Damage) != ranker.Damage)
                    throw new InvalidDataException("Invalid museum NPC damage or squad snapshot.");
        }
        return archive;
    }

    private static Stage? Find(int stageId) => Data.Value.Stages.FirstOrDefault(s => s.StageId == stageId);

    public static int ParticipantCount(int stageId) => Find(stageId)?.Rankers.Count ?? 0;

    public static long PersonalRank(int stageId, long damage) => damage <= 0 ? 0 :
        Find(stageId) is { } stage ? stage.Rankers.Count(r => r.Damage > damage) + 1 : 0;

    public static IEnumerable<NetSoloRaidMuseumRankerRankingData> GetRankings(int stageId)
    {
        if (Find(stageId) is not { } stage) return [];
        return stage.Rankers.Where(r => DisplayRanks.Contains(r.Ranking)).OrderByDescending(r => r.Ranking)
            .Select(r => new NetSoloRaidMuseumRankerRankingData
            {
                Ranking = r.Ranking,
                // The museum client subtracts the ordinary-stage HP from a
                // historical total. Squad details contain challenge damage only.
                Damage = checked(stage.CommonHp + r.Damage),
                PastUserData = new NetPastWholeUserData
                {
                    Usn = r.Usn, Nickname = r.Nickname, Icon = r.Icon, Frame = 1,
                    GuildName = "NPC", UserTitleDisplayData = new(),
                },
                CurrentUserData = new NetWholeUserData
                {
                    Usn = r.Usn, Nickname = r.Nickname, Icon = r.Icon, Frame = 1,
                    GuildName = "NPC", Lv = 400, Server = 1001, UserTitleDisplayData = new(),
                },
            }).ToArray();
    }

    public static IEnumerable<NetSoloRaidMuseumLog> GetSquads(int stageId, long targetUsn)
    {
        var ranker = Find(stageId)?.Rankers.FirstOrDefault(r => r.Usn == targetUsn);
        if (ranker is null) return [];
        // Clone every protobuf object: callers must never mutate the archive.
        return ranker.Squads.Select(s => new NetSoloRaidMuseumLog
        {
            TeamNumber = s.TeamNumber, Damage = s.Damage,
            Team = { Data.Value.Lineups[s.Lineup].Select(c => c.Clone()) },
        }).ToArray();
    }
}
