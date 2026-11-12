# Automatic Solo Raid Synchronization Plan

> **For agentic workers:** Use superpowers:executing-plans in the registered session worktree. Verify the whole change with an independent read-only reviewer.

**Goal:** Automatically keep future solo raid Boss selections and fixed dates aligned with NikkeHub, and visibly request game data updates when needed.

**Architecture:** A bounded public-page JSON reader feeds an atomically persisted schedule catalog. A hosted hourly worker updates that catalog; battle helpers resolve immutable snapshots and never change saves while syncing. Existing fixed configuration remains an offline fallback.

**Tech Stack:** .NET10, existing HttpClient/background-service infrastructure, Newtonsoft.Json, existing protocol/ASP.NET regression harness.

**Spec:** `docs/superpowers/specs/2026-11-12-solo-raid-auto-sync-design.md`.

## Global Constraints

- User's exact source: https://nikkehub.com/zh-tw/raids/solo . Startup + hourly, no external task scheduler.
- Fetch only public schedules; no script execution or account/credential transmission.
- Only locally complete Boss stages may activate; otherwise prompt to update game data.
- Keep fixed UTC cutoffs, existing records, daily reset rules, and the original game startup flow.
- Work/test only in this session; use the project's unified integration, verification, release and deployment tools.

## Review Focus

- Source/parser failure and offline restart retain the last confirmed fixed cutoff.
- Announced future or missing Bosses never become playable early or get replaced by a different Boss.
- Source corrections and stale responses cannot silently move users onto an older season.
- A rollout at the start boundary cannot mix an old manager with a new period, or credit late old damage to a fresh season.
- Update messages render external text safely and disappear when the missing local data becomes available.

### Task 1: Source/catalog regressions and implementation

Files: new `NikkeHubSoloRaidSource.cs`, `SoloRaidSeasonCatalog.cs`, `SoloRaidSeasonSource.cs` under `EpinelPS/LobbyServer/Soloraid`; new `tests/SoloRaidAutoSyncRegression`.

- [ ] Write parser/catalog behavior tests first and observe missing automatic source behavior through a compiling reflection entry; switch to typed tests after the API exists.
- [ ] Implement bounded, nonexecuting JSON-model parsing and HTTP fetch from the fixed source. Source record: season number, plain Boss name, fixed start/exclusive end.
- [ ] Implement validation, atomic cache publication/reload, matching by preset group, pending missing data, future activation and no backward switch. Test network/cache failures and restart.
- [ ] Commit and run `nikke_workflow.py test --worktree <session source> --suite SoloRaidAutoSyncRegression`.

### Task 2: Runtime, visible update hint and protocol behavior

Files: `Program.cs`, `Utils/GameConfig.cs`, new `Services/SoloRaidSeasonSyncWorker.cs`, `SoloRaidHelper.cs`, `Controllers/LevelInfiniteControlller.cs`, `Controllers/AdminApiController.cs`, `Views/Admin/Dashboard.cshtml`, relevant regression programs.

- [ ] Load cache and bind the catalog before requests; refresh in a background worker at startup/hourly, with bounded cancellation and failure handling.
- [ ] Resolve/capture one configured or synchronized season per operation. Reject late damage without a matching current open run before mutation.
- [ ] Add the requested missing-data hint to system notices, dashboard and logs; expose authenticated read-only sync status for the dashboard. Keep names encoded.
- [ ] Test protocol rotation and unchanged old records, missing-data notice and recovery, opt-out, and actual public-source parsing using a read-only probe.
- [ ] Commit and request independent whole-change review, resolving substantive findings with repro tests.

### Task 3: Delivery

- [ ] Integrate/sync and run `test --suite all`.
- [ ] Audit new history for saves/keys/tokens; push the integration ref to the Fork and verify `ls-remote`.
- [ ] Publish; prepare a new installer with backup/rollback for the cache as well as program/saves and authenticated/source validation.
- [ ] Seal/check; verify game exit and deploy solely via unified entry. Confirm successful actual source sync, exact fixed period, preserved records and startup health.
- [ ] Report observed status, where update hints appear, website/game-data limitations, and any remaining client visual acceptance.
