# Rookie Matchmaking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make higher-ranked rookie opponents reachable without combat-power exclusion, validate and deploy.

**Architecture:** Keep special matching intact. Add a rookie-only selector using the current ranked participants, three candidate roles and the existing persisted refresh cursor.

**Tech Stack:** C#/.NET 10, existing ArenaRegression HTTP fixture, PowerShell installer, project nikke_workflow.py.

**Spec:** docs/superpowers/specs/2026-11-14-rookie-matchmaking-design.md

## Global Constraints

- All source/build/test operations use this session's sourceDirectory and workspaceDirectory.
- No player data, credentials or runtime snapshots in commits.
- Rookie matching uses all valid participants, excludes self, ignores combat power and NPC status, returns up to three distinct entries in rating-descending/ID-ascending order.
- Select higher rank, nearest remaining rating cohort and lower rank; fill missing slots from nearest remaining rating cohorts. Stable hash order per user/season/role and RookieRefresh rotation.
- Existing special matching and rating/reward/ticket logic stay intact. No save schema changes.
- Only latest integrated, synchronized and validated source may publish; deploy only a sealed manifest under the shared lock after game exit.

## Review Focus

- Equal ratings and ID tie order: more than twelve peers must remain reachable.
- Empty/one/two-opponent populations: fill available slots without duplicates.
- Large score and combat differences: higher-ranked humans and NPCs must remain reachable.
- Banned, locked or malformed rosters: never bypass existing eligibility.
- Old/overflowing cursors and mode separation: refresh must persist without changing attempts, rewards, special cursor or special opponents.

### Task 1: Reproduce and fix rookie candidate selection

**Files:** Modify EpinelPS/LobbyServer/Arena/ArenaService.cs; create EpinelPS/LobbyServer/Arena/ArenaMatchmaking.cs and tests/ArenaRegression/RookieMatchmakingChecks.cs; invoke the checks from tests/ArenaRegression/Program.cs.

**Interfaces:** Existing /arena/getenemylist response and existing ArenaPlayer.RookieRefresh; private IEnumerable<User> RookieRivals() in the ArenaService partial class.

- [ ] Add HTTP regressions for every Review Focus item, commit tests and run `nikke_workflow.py test --worktree <sourceDirectory> --suite ArenaRegression`; observe matching assertions fail on old code.
- [ ] Implement rookie-only stratified rotation with overflow-safe cursor, deterministic hash order and sorted output; commit and rerun ArenaRegression until green.
- [ ] Request one independent branch review, address material findings and record validation in this plan.

### Task 2: Integrate, validate and deploy

**Files:** Own .nikke installer and release evidence, existing tracked plan only for validation notes.

- [ ] Integrate/sync using nikke_workflow.py, then run ArenaRegression and related ArenaCubeRegression; run the full regression set before publishing.
- [ ] Inspect newly pushed history for secrets/saves, push the exact integration commit to the authorized fork branch, confirm using ls-remote.
- [ ] Publish latest integration, prepare and review a new installer with exit check, stopped-server backup, rollback and health/data-preservation checks, then seal and check-release.
- [ ] Once the game has exited, deploy through nikke_workflow.py, verify installed hash/HTTP/TLS, inspect an isolated post-install save snapshot and report that client acceptance remains pending.
