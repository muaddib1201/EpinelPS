using EpinelPS.Data;
using EpinelPS.Database;
using EpinelPS.LobbyServer.Guild;
using EpinelPS.LobbyServer.Mission;
using EpinelPS.Utils;
using Newtonsoft.Json;

namespace EpinelPS.Services;

public static partial class ManagedNpcService
{
    public const int DailyPhaseCount = 5;
    // Finish by minute 25 so the minute worker and settlement have a five-minute margin.
    private static readonly TimeSpan PhaseInterval = TimeSpan.FromMinutes(5);

    // Player requests share JsonDb.SaveLock with this worker. Each NPC settlement holds the
    // lock only for its in-memory change; the worker then pauses so waiting requests run,
    // and writes the whole save in one batch instead of once per NPC phase.
    internal static TimeSpan SettlementPause { get; set; } = TimeSpan.FromMilliseconds(15);
    internal static TimeSpan CommitInterval { get; set; } = TimeSpan.FromSeconds(10);
    private static volatile bool unsavedSettlements;

    public static void Tick(DateTime now) => TickAsync(now).GetAwaiter().GetResult();

    public static async Task TickAsync(DateTime now, CancellationToken cancellation = default)
    {
        // Resolve immutable raid metadata once for this population update, and only
        // if an eligible NPC actually reaches the raid action.
        var soloRaid = new Lazy<NpcRaidSchedule?>(() => NpcRaidScheduleAt(now));
        ulong[] ids;
        lock (JsonDb.SaveLock)
            ids = JsonDb.Instance.Users.Where(u => u.ManagedNpc != null).Select(u => u.ID).Order().ToArray();
        var batch = new SettlementBatch();
        // Settlements from a tick whose save failed are still only in memory.
        if (unsavedSettlements) batch.RetrySave();
        Exception? firstFailure = null;
        bool commitFailed = false;
        foreach (ulong id in ids)
        {
            if (cancellation.IsCancellationRequested) break;
            try
            {
                // One settlement per lock acquisition, so catch-up phases also let players in.
                while (SettleNext(id, now, soloRaid, batch)) await Task.Delay(SettlementPause);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
                Logging.WriteLine($"Managed NPC {id} phase failed and remains pending: {ex.Message}", LogType.Error);
            }
            if (batch.Due && !commitFailed)
            {
                try { await batch.CommitAsync(); }
                catch (Exception ex) { firstFailure ??= ex; commitFailed = true; LogCommitFailure(ex); }
            }
            await Task.Delay(SettlementPause);
        }
        // Also commit after a cancelled shutdown, so already settled phases are kept.
        if (batch.Pending)
        {
            try { await batch.CommitAsync(); }
            catch (Exception ex) { firstFailure ??= ex; LogCommitFailure(ex); }
        }
        // Surface infrastructure failures to the worker after every healthy NPC had its turn.
        if (firstFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }

    private static void LogCommitFailure(Exception ex) => Logging.WriteLine(
        $"Managed NPC settlements remain in memory; save will be retried: {ex.Message}", LogType.Error);

    // Settles at most one unit for this NPC. Returns true when another unit may be due.
    private static bool SettleNext(ulong id, DateTime now, Lazy<NpcRaidSchedule?> soloRaid, SettlementBatch batch)
    {
        var user = JsonDb.GetUser(id);
        if (user == null) return false;
        lock (user)
        lock (JsonDb.SaveLock)
        {
            if (!ReferenceEquals(JsonDb.GetUser(id), user)) return false;
            // Recovery remains active even after today's routine has finished. Queued
            // notifications are delivered only once this batch is durable.
            if (user.PendingMissionTriggers.Count > 0) batch.Notify(user);
            var settings = JsonDb.Instance.NpcManagement;
            var npc = user.ManagedNpc!;
            if (settings?.Enabled != true || !npc.Enabled || now < settings.LastProcessedUtc || now < npc.LastActionUtc) return false;
            int day = GuildActivity.Day(now);
            var plan = npc.DailyRoutine;
            if (plan == null || plan.Day < day)
            {
                SettleDailyPhase(user, now, false, batch, () =>
                {
                    GuildActivity.EnsureMember(user, now);
                    npc.DailyRoutine = new NpcDailyRoutineState { Day = day, StartedUtc = now, NextPhaseUtc = now + PhaseInterval };
                    npc.NextActionUtc = npc.DailyRoutine.NextPhaseUtc;
                    npc.Day = day;
                    npc.StagesToday = npc.LevelsToday = npc.ActivityToday = npc.ShootingToday = 0;
                });
                return false;
            }
            if (plan.Day != day || now < plan.LastCompletedUtc) return false;
            if (plan.CompletedPhases < DailyPhaseCount && plan.NextPhaseUtc <= now)
            {
                int phase = plan.CompletedPhases + 1;
                SettleDailyPhase(user, now, phase is 1 or 5, batch, () =>
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
                return true;
            }
            // A season may open after today's other activities have finished.
            // Its own checkpoint also lets existing completed routines join after an update.
            if (plan.CompletedPhases >= 3) RunSoloRaidDaily(user, now, soloRaid, batch);
            return false;
        }
    }

    // One owner for resources, phase checkpoint, guild mutations and durable trigger outbox.
    // A failed action is rolled back while the locks are still held. The change itself is
    // written by the batch; until then a crash simply leaves the phase unsettled on disk.
    private static void SettleDailyPhase(User user, DateTime now, bool sharedPlayers, SettlementBatch batch, Action action)
    {
        var core = JsonDb.Instance;
        // Serialize only; the objects are rebuilt when an action actually fails.
        var backups = (sharedPlayers ? core.Users : [user])
            .Select(u => (Target: u, Json: JsonConvert.SerializeObject(u), Currency: new Dictionary<CurrencyType, long>(u.Currency))).ToArray();
        var guilds = JsonConvert.SerializeObject(core.Guilds);
        var arena = core.Arena == null ? null : JsonConvert.SerializeObject(core.Arena);
        try
        {
            using var scope = new RewardMutationScope(user, now);
            action();
            user.PendingMissionTriggers.AddRange(scope.Triggers);
        }
        catch
        {
            foreach (var (target, json, currency) in backups)
            {
                var saved = JsonConvert.DeserializeObject<User>(json)!;
                saved.Currency = currency;
                foreach (var p in typeof(User).GetProperties().Where(p => p.CanRead && p.CanWrite)) p.SetValue(target, p.GetValue(saved));
                foreach (var f in typeof(User).GetFields()) f.SetValue(target, f.GetValue(saved));
            }
            core.Guilds = JsonConvert.DeserializeObject<Dictionary<int, SavedGuild>>(guilds)!;
            core.Arena = arena == null ? null : JsonConvert.DeserializeObject<ArenaWorld>(arena);
            throw;
        }
        batch.Settled(user);
    }

    // NPC settlements stay in memory until one batched save. Their SQLite notifications are
    // delivered only after that save succeeds, so a crash cannot notify an unsaved phase.
    private sealed class SettlementBatch
    {
        private readonly HashSet<User> notify = [];
        private readonly System.Diagnostics.Stopwatch age = new();
        private bool dirty;
        internal bool Due => dirty && age.Elapsed >= CommitInterval;
        internal bool Pending => dirty || notify.Count > 0;
        internal void Notify(User user) => notify.Add(user);
        internal void RetrySave() => dirty = true;
        internal void Settled(User user)
        {
            if (!dirty) age.Restart();
            dirty = true;
            notify.Add(user);
        }

        internal async Task CommitAsync()
        {
            try { lock (JsonDb.SaveLock) JsonDb.Save(); }
            catch { unsavedSettlements = true; throw; }
            unsavedSettlements = false;
            dirty = false;
            var users = notify.ToArray();
            notify.Clear();
            bool delivered = false;
            foreach (var user in users)
            {
                delivered |= MissionProgressDelivery.Flush(user, save: false);
                await Task.Delay(SettlementPause);
            }
            // Delivery is idempotent; if this save is lost the queue is delivered again.
            if (delivered) lock (JsonDb.SaveLock) JsonDb.Save();
        }
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
