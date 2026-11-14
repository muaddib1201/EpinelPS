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

- [x] Add HTTP regressions for every Review Focus item, commit tests and run `nikke_workflow.py test --worktree <sourceDirectory> --suite ArenaRegression`; observe matching assertions fail on old code.
- [x] Implement rookie-only stratified rotation with overflow-safe cursor, deterministic hash order and sorted output; commit and rerun ArenaRegression until green.
- [x] Request one independent branch review, address material findings and record validation in this plan.

### Task 2: Integrate, validate and deploy

**Files:** Own .nikke installer and release evidence, existing tracked plan only for validation notes.

- [ ] Integrate/sync using nikke_workflow.py, then run ArenaRegression and related ArenaCubeRegression; run the full regression set before publishing.
- [ ] Inspect newly pushed history for secrets/saves, push the exact integration commit to the authorized fork branch, confirm using ls-remote.
- [ ] Publish latest integration, prepare and review a new installer with exit check, stopped-server backup, rollback and health/data-preservation checks, then seal and check-release.
- [ ] Once the game has exited, deploy through nikke_workflow.py, verify installed hash/HTTP/TLS, inspect an isolated post-install save snapshot and report that client acceptance remains pending.

## Validation before integration

- Original candidate filtering: four new HTTP assertions failed on the old implementation (higher-rank reachability, score roles, combat invariance and legacy negative cursor).
- Independent review found a lower-rank permutation/filtering interaction. A season-zero mixed-gap HTTP fixture reproduced permanent exclusion; rotating the complete group before skipping already-selected entries fixes it. Fixture SQL rows are cleaned up so they do not interfere with the existing late-entrant regression.
- Final code at 67bbb566ba07f8e892fafea7bb125e237cb33e53 passed ArenaRegression 54 default + 4 restart groups and ArenaCubeRegression 21 default + 1 restart groups (80 groups). Evidence: own .nikke/checks/20261114-234311-832299/tests.json.
- Independent installer review led to SQLite integrity and logical table comparison through a consistent read-only online backup. A synthetic harness against the actual verification functions passed six cases: equal databases, modified Users/SdkUsers/Triggers, missing database and corrupt database. JSON-only verification had accepted the three modified-table fixtures before the fix.
- An earlier isolated actual-save audit of the new higher-rank path at screenshot score 1807 offered all nine 1820-score rivals in 24 refreshes, versus none before the fix. A fresh audit will accompany the final release.
