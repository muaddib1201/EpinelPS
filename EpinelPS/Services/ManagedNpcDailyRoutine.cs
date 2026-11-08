using EpinelPS.Database;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Utils;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    public const int DailyPhaseCount = 5;
    private static readonly TimeSpan PhaseInterval = TimeSpan.FromMinutes(6);

    public static void Tick(DateTime now)
    {
        ulong[] ids;
        lock (JsonDb.SaveLock)
            ids = JsonDb.Instance.Users.Where(u => u.ManagedNpc != null).Select(u => u.ID).Order().ToArray();
        foreach (ulong id in ids)
        {
            var user = JsonDb.GetUser(id);
            if (user == null) continue;
            lock (user)
            lock (JsonDb.SaveLock)
            {
                if (!ReferenceEquals(JsonDb.GetUser(id), user)) continue;
                // Recovery remains active even after today's routine has finished.
                MissionProgressDelivery.Flush(user);
                var settings = JsonDb.Instance.NpcManagement;
                var npc = user.ManagedNpc!;
                if (settings?.Enabled != true || !npc.Enabled || now < settings.LastProcessedUtc || now < npc.LastActionUtc) continue;
                int day = GuildActivity.Day(now);
                var plan = npc.DailyRoutine;
                if (plan == null || plan.Day < day)
                {
                    SettleDailyPhase(user, now, false, () =>
                    {
                        GuildActivity.EnsureMember(user, now);
                        npc.DailyRoutine = new NpcDailyRoutineState { Day = day, StartedUtc = now, NextPhaseUtc = now + PhaseInterval };
                        npc.NextActionUtc = npc.DailyRoutine.NextPhaseUtc;
                        npc.Day = day;
                        npc.StagesToday = npc.LevelsToday = npc.ActivityToday = npc.ShootingToday = 0;
                    });
                    continue;
                }
                if (plan.Day != day || now < plan.LastCompletedUtc) continue;
                while (plan.CompletedPhases < DailyPhaseCount && plan.NextPhaseUtc <= now)
                {
                    int phase = plan.CompletedPhases + 1;
                    SettleDailyPhase(user, now, phase is 1 or 5, () =>
                    {
                        user.ResetDataIfNeeded();
                        ExecuteDailyPhase(user, phase, now);
                        plan.CompletedPhases = phase;
                        plan.LastCompletedUtc = now;
                        plan.NextPhaseUtc += PhaseInterval;
                        npc.LastActionUtc = user.LastLogin = now;
                        npc.TotalActions++;
                        npc.Sequence++;
                        npc.NextActionUtc = phase == DailyPhaseCount ? GuildActivity.DayStart(now).AddDays(1) : plan.NextPhaseUtc;
                        Log(npc, now, $"每日计划：阶段 {phase}/{DailyPhaseCount} 已结算");
                    });
                }
            }
        }
    }

    // One owner for resources, phase checkpoint, guild mutations and durable trigger outbox.
    private static void SettleDailyPhase(User user, DateTime now, bool sharedPlayers, Action action)
    {
        var core = JsonDb.Instance;
        var backups = (sharedPlayers ? core.Users : [user]).ToDictionary(u => u, u =>
        {
            var saved = Clone(u);
            saved.Currency = new(u.Currency);
            return saved;
        });
        var guilds = core.Guilds.ToDictionary(g => g.Key, g => Clone(g.Value));
        var arena = core.Arena == null ? null : Clone(core.Arena);
        try
        {
            using (var scope = new RewardMutationScope(user, now))
            {
                action();
                user.PendingMissionTriggers.AddRange(scope.Triggers);
            }
            JsonDb.Save();
        }
        catch
        {
            foreach (var (target, saved) in backups)
            {
                foreach (var p in typeof(User).GetProperties().Where(p => p.CanRead && p.CanWrite)) p.SetValue(target, p.GetValue(saved));
                foreach (var f in typeof(User).GetFields()) f.SetValue(target, f.GetValue(saved));
            }
            core.Guilds = guilds;
            core.Arena = arena;
            throw;
        }
        MissionProgressDelivery.Flush(user);
    }

    private static void RescheduleDailyRoutine(ManagedNpcState npc, DateTime now)
    {
        if (npc.DailyRoutine is { CompletedPhases: < DailyPhaseCount } plan)
            plan.NextPhaseUtc = npc.NextActionUtc = now + PhaseInterval;
        else npc.NextActionUtc = GuildActivity.DayStart(now).AddDays(1);
    }

    private static void ExecuteDailyPhase(User user, int phase, DateTime now)
    {
        switch (phase)
        {
            case 1: CollectDailyResources(user, now); break;
            case 2: CounselAndTrainDaily(user, now); break;
            case 3: RecruitAndResourceBattlesDaily(user, now); break;
            case 4: AdvanceDailyProgress(user, now); break;
            case 5:
                CompleteDailySocial(user, now);
                ClaimDailyRoutineMissions(user, now);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(phase));
        }
    }
}
