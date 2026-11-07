# Managed NPC Players Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Create 30 independently growing NPC players and three usable NPC unions, with persistent scheduling and administrator controls.

**Architecture:** Extend the existing JSON save with optional NPC state, scheduler settings and a recoverable creation batch. Store only GameUser profile rows in SQLite. A single hosted worker advances due NPCs, while existing profile/union/shooting-range routes expose their current data.

**Tech Stack:** C#, .NET 10, ASP.NET Core MVC, EF Core SQLite, Newtonsoft.Json, existing protobuf protocol.

**Spec:** ../specs/2026-11-06-managed-npc-players-design.md (approved by the user).

## Global Constraints

- Preserve all existing source changes, current configuration and real-player saves.
- Defaults: 30 NPCs, 3 unions, 8 initial members per union, 6 initially unaffiliated; at most 200 NPCs; speed 0.25–4.
- Progress is server-side rule simulation; exclude arena, friends, union raid and complete resource economics.
- Isolated JSON/SQLite for tests; actual game acceptance is separate from regression results.
- No SdkUser credentials for NPCs; no automatic deletion; pause accrues no catch-up.
- At most 72 hours of catch-up and 50 NPC action records; keep existing shooting history limit 100.

## Review Focus

- Interrupted creation must never duplicate NPCs, overwrite humans or leave profiles missing.
- Restart, a backward clock and repeated ticks must not grant duplicate daily progress/contributions.
- Pause/resume and changing speed must not convert paused hours into a large burst of growth.
- Runtime additions and progress updates must not invalidate profile/member enumeration or deadlock existing locks.
- Malformed admin values and union membership transitions must fail without partial state changes.

### Task 1: Recoverable NPC creation and persistent state

**Files:** Create Models/ManagedNpc.cs, Services/ManagedNpcService.cs, tests/ManagedNpcRegression/{Program.cs,ManagedNpcRegression.csproj}; modify Models/{UserModel.cs,CoreInfoModel.cs,DbModels.cs}, Data/GameData.cs.

**Interfaces:** public ManagedNpcService.Create(GameContext db, int count, DateTime now), Recover(GameContext db); public User.ManagedNpc, CoreInfo.NpcManagement; SavedGuild.ManagedNpcGuild.

- [x] Write/run failing isolated tests for initial counts, valid owned five-character squads, distinct progress/identity, missing SDK credentials, idempotent recovery, UID collision and preserved humans.
- [x] Implement models, table-driven templates and a journaled SQL profile/JSON commit sequence; add GuildTable loading.
- [x] Run the creation tests and existing union baseline.

### Task 2: Bounded simulation and background worker

**Files:** Create Services/{ManagedNpcSimulation.cs,ManagedNpcWorker.cs}; modify Services/ManagedNpcService.cs, Program.cs, LobbyServer/{LobbyHandler.cs,LobbyUser/GetUserProfile.cs,Guild/GuildDirectory.cs}.

**Interfaces:** ManagedNpcService.Tick(DateTime now), Configure(bool enabled,double speed,DateTime now), Update(ulong id,bool enabled,int tier,int? guildId,DateTime now). Simulation operates only on staged clones committed under the JSON lock; profile activity derives from NPC state.

- [x] Write/run failing tests for daily caps, deterministic repeat/restart, 72-hour catch-up, bounded logs, varied progress, pause semantics, backward clocks and failed-save rollback.
- [x] Implement table-driven stage/character/skill growth, daily union activity and sparse shooting-range results; schedule independent active windows and persist each due action with its next due time.
- [x] Add the worker after database initialization and recovery; ensure startup reconciliation skips non-login NPC task triggers.
- [x] Run scheduler and creation tests; check relevant reads under the established lock order.

### Task 3: Union interaction and NPC admin controls

**Files:** Modify LobbyServer/Controllers/GuildController.cs, LobbyServer/Guild/GuildDirectory.cs, Views/Shared/_Layout.cshtml; create Controllers/AdminPanel/ManagedNpcsController.cs, Views/ManagedNpcs/Index.cshtml, Models/Admin/ManagedNpcModels.cs.

**Interfaces:** Existing protocol join/leave messages at client protocol routes; GET /admin/ManagedNpcs, antiforgery-protected POST create/configure/update actions.

- [x] Write/run failing HTTP tests for open-union join/leave, locked/full/duplicate membership, master leave rejection, unauthorized admin requests and missing antiforgery tokens.
- [x] Implement union transitions with rollback, limits from GuildTable and current game settings; never move humans automatically.
- [x] Implement status table, global settings, per-NPC pause/tier/union controls, add/default creation and navigation link.
- [x] Run new HTTP tests plus existing GuildRegression and ShootingRangeRegression.

### Task 4: Review, publish, install and verify

- [x] Run fresh review of changed files against spec; fix consequential findings and verify with targeted regression tests.
- [x] Run ManagedNpcRegression, GuildRegression and ShootingRangeRegression including restart passes.
- [x] Publish the complete current source with the bundled SDK to a dedicated .tools/managed-npc directory.
- [x] Back up deployed program and consistent saves. If the client is running, stage an install watcher; otherwise stop the server safely, install and seed exactly once, restart and inspect status.
- [x] Report actual installation and tests, keeping pending client acceptance explicit.
