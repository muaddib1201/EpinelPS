# Rookie Arena Dynamic Rating Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Apply rating-gap-dependent rookie arena changes to players and NPCs.

**Architecture:** Compute one Elo-like winner/loser transfer in the shared FinishRookie settlement. Preserve existing transactions, receipts, records, rewards, and stored ratings.

**Tech Stack:** C# / .NET 10, existing executable regression harness and project collaboration workflow.

**Spec:** docs/superpowers/specs/2026-11-13-rookie-rating-design.md

## Global Constraints

- Work only in session 01a1d0bc-0ea9-7650-8b25-2237bfaea895; no live-save writes during tests.
- K=40, scale=400, round away from zero, transfer 1..40, stored ratings 0..Int32.MaxValue. These are approximate parameters.
- All participant types use the same pre-settlement ratings and transfer.
- No migration, special-arena changes, reward changes, or matchmaking changes.

## Review Focus

- Unequal ratings and either result must give mirrored attack/defense changes.
- Current settlement ratings, not a stale opponent snapshot, determine the transfer.
- Zero and extreme integer ratings must not wrap or become negative.
- Duplicate and restarted requests must not settle a second time.
- Autonomous NPC routines must call the shared calculation.

## Task 1: Shared dynamic settlement and regression coverage

**Files:** Modify EpinelPS/LobbyServer/Arena/ArenaBattles.cs and tests/ArenaRegression/Program.cs.

**Interfaces:** Existing Result / FinishRookie / RunNpcRoutine remain unchanged. Add a private static winner-transfer helper to ArenaService.

- [x] Add table-driven HTTP cases for equal ratings, +/-80 and +/-400 gaps, both outcomes, human/NPC combinations, floor and integer ceiling. Assert literal before/after ratings, both records, durable saves, and idempotent replies.
- [x] Add a restarted receipt assertion and one autonomous NPC match with 1720 vs 1800 -> 1745 vs 1775.
- [x] Run `nikke_workflow.py test --worktree <session source> --suite ArenaRegression`. Observed: 28 passed, 11 expected rating failures against fixed scoring (20261113-012256-376510).
- [x] Implement the spec's single-transfer calculation with wide addition and score bounds.
- [ ] Repeat ArenaRegression. Expected: all default and saved-state cases pass.
- [ ] Obtain independent review, address material findings, then commit.
- [ ] Integrate and sync through nikke_workflow.py, run ArenaRegression, ArenaCubeRegression, NpcDailyRoutineRegression, and ManagedNpcRegression. Expected: no failures.
- [ ] Check added history for secrets and saves; push the exact integration commit to the authorized Fork branch and verify via ls-remote.
- [ ] Publish and seal an installer with game-exit checks, backup, rollback, and health checks. Install only within current authorization using deploy.
