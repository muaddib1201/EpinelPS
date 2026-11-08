# NPC Daily Routine Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans in this session. Steps use checkbox syntax.

**Goal:** Replace random free NPC growth with one persistent five-phase daily routine completed within thirty minutes, settling actual rewards, mission points and union contributions exactly once.

**Architecture:** Keep one hosted timer; persist daily plan state on existing NPCs. Phase actions reuse business helpers, capture notifications in a durable outbox, and atomically save settlement with phase completion before SQLite delivery.

**Tech Stack:** C#/.NET 10, JSON, SQLite, console regression projects.

**Spec:** docs/superpowers/specs/2026-11-08-npc-daily-routine-design.md

## Global Constraints

- Only this session's registered worktree, isolated fixtures, and uniform workflow entry points.
- Preserve existing NPC identities, character inventory, progress, teams and guilds; no resets or free growth.
- One routine per game day, phases due at 5/10/15/20/25 minutes with five minutes reserved for polling and settlement; restart never repeats settlement.
- Real resource costs/rewards and eligible mission claims; guild contribution only from actual daily task points.
- No client/battle simulation, live test writes, fabricated unsupported actions or historical daily catchup.

## Review Focus

- JSON save succeeds but SQLite delivery fails: replay notifications only.
- Phase failure after touching opponents/world/guild: restore all affected objects.
- Same timestamp, clock rollback, pause/resume and midnight: no duplicate routine or free extra phases.
- Multiple NPC simulation rewards: never mutate shared static reward rows.
- Existing tower and mission trigger history plus pending outboxes: no repeated first-clear/claim.

### Task 1: Daily state and transaction-safe scheduler

**Files:** Models/ManagedNpc.cs, Services/ManagedNpcSimulation.cs, Services/ManagedNpcDailyRoutine.cs, Utils/RewardMutationScope.cs; tests/NpcDailyRoutineRegression/*.

**Interfaces:** Keep ManagedNpcService.Tick(DateTime), Configure and Update. Add persistent NpcDailyRoutineState and pure phase execution boundary receiving User, phase, DateTime. Existing users without state migrate lazily.

- [ ] Add and run failing behavior regressions for phase timing, once/day, preservation and restart.
- [ ] Implement scheduler and atomic phase boundary, ambient action timestamp, outbox recovery.
- [ ] Verify timing/rollback/pause/cross-day regressions and commit.

### Task 2: Actual resource activities and progression

**Files:** Services/ManagedNpcDailyActions*.cs; minimal extraction of existing counsel/gacha/tower/simroom/interception/arena/dispatch helpers.

**Interfaces:** ExecutePhase(User user, int phase, DateTime now), using the transaction from Task 1. Shared helper functions remain compatible with client routes.

- [ ] Add failing assertions for actual resources, cost accounting, bond/recruit/progression and pending-trigger behavior.
- [ ] Implement five phases with real helpers, unlock/attempt/cost guards and bounded activity logs.
- [ ] Verify affected gameplay regressions and static reward immutability, then commit.

### Task 3: Mission claims, admin status and integration

**Files:** Services/ManagedNpcDailyMissions.cs, Views/ManagedNpcs/Index.cshtml, existing NPC regression expectation updates, new integration tests.

**Interfaces:** Task 2 actions produce real triggers; this task evaluates actual daily/weekly/achievement eligibility, claims rewards and invokes GuildActivity once per task point increment.

- [ ] Add failing end-to-end assertions for task points, guild experience/contribution, repeat claims and save/SQLite failures.
- [ ] Implement eligible claim flow, update admin phase/progress labels and replace old random-growth test expectations.
- [ ] Run full suite, independent review, fix substantive findings, commit, integrate, sync and run integrated tests/build.

Routine outputs and progress evidence live under this session workspace. Installation is a separate serialized deployment step with backup, rollback, game-exit and health checks.
