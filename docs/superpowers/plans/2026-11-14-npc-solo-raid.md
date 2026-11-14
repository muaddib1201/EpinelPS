# NPC Solo Raid Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan in the current registered worktree, followed by a fresh whole-branch review.

**Goal:** Managed NPCs advance the active Solo Raid using their own teams and publish completed challenge scores through the existing ranking endpoint.

**Architecture:** Add a separately checkpointed raid activity to ManagedNpcService.Tick after phase 3, within its existing save/rollback boundary. A focused NPC raid module resolves presets, forms owned squads, estimates damage and settles normal/challenge records without intermediate saves. Reuse FormulaUtils with a nonmutating explicit-level overload.

**Tech Stack:** C#/.NET 10, JSON account storage, existing SQLite profile/mission store, console regression projects, project nikke_workflow.py.

**Spec:** docs/superpowers/specs/2026-11-14-npc-solo-raid-design.md

## Global Constraints

- Work only in sourceDirectory registered for session 01a1d8f1-f9aa-7011-852f-dc5e8bbcc322; all fixtures/output stay in its workspaceDirectory.
- Use the unified test/integrate/sync/publish/seal/deploy entries. Never load a live save into a mutating test process.
- Preserve human accounts, existing squads and historical seasons. No nested saves inside an NPC settlement.
- Respect unlocks, active season, daily budgets and checkpoint/restart semantics. Character-level overrides must not change saved levels.
- After successful integration/sync/verification, push the exact full integration history to muaddib1201/EpinelPS nikke/integration and verify ls-remote.

## Review Focus

- A season opens after today's five daily phases: exactly one new raid activity is allowed without replaying other phases.
- A roster has fewer than 25 usable characters or duplicate character variants: do not reuse or invent characters.
- Partial/invalid local boss data: skip safely without starving the rest of the NPC population.
- A run performs worse than a prior best: preserve the old damage and squad snapshots while consuming today's actual attempts.
- Saving fails after rewards would be granted: roll back checkpoint and resources together; a fresh process must not replay committed work.

### Task 1: Implement and verify raid participation

**Files:**
- Create: EpinelPS/Services/ManagedNpcSoloRaid.cs
- Modify: EpinelPS/Services/ManagedNpcDailyRoutine.cs
- Modify: EpinelPS/Models/ManagedNpc.cs
- Modify: EpinelPS/Utils/FormulaUtils.cs
- Create: tests/NpcSoloRaidRegression/NpcSoloRaidRegression.csproj and Program.cs

**Interfaces:**
- Consumes: ManagedNpcService.Tick(DateTime now), SettleDailyPhase, DailyUnlocked, GameData tables, SoloRaidSeasonSource, SoloRaidData, RewardUtils and FormulaUtils.CalculateCP.
- Produces: persisted per-season/day NPC checkpoint, ordinary progression and completed Trial records usable by SoloRaidHelper.GetSoloRaidRanking. Explicit-level CP overload does not mutate the user.

- [ ] Write a failing regression using Tick on an NPC with completed daily phases and seven saved ordinary clears; expect owned-squad challenge records and a ranked entry.
- [ ] Run `python D:\Claude Lab\NIKKE\.tools\collaboration\nikke_workflow.py test --worktree <sourceDirectory> --suite NpcSoloRaidRegression`; inspect failure and the fixture's local raid settings/presets.
- [ ] Add focused scenarios for own-power ordering, ordinary progression/rewards/budgets, challenge delay, roster identity/uniqueness, best retention, skip conditions, restart and save-failure rollback. Run to establish missing behavior.
- [ ] Implement the module, scheduler/checkpoint and nonmutating CP overload; make all targeted scenarios pass.
- [ ] Run related NpcDailyRoutineRegression, ManagedNpcRegression, SoloRaidRankingRegression and SoloRaidAutoSyncRegression through the unified test entry. Record evidence and commit.

### Task 2: Review, integrate, back up and prepare release

- [ ] Obtain a fresh review of the full branch against the spec and Review Focus. Reproduce actionable defects with failing tests and fix them.
- [ ] Integrate, sync and run the complete unified regression suite. Publish from the latest clean integration commit.
- [ ] Audit outgoing files/history, push the exact integration commit to the authorized Fork branch, and verify its remote hash.
- [ ] Prepare a new session-owned installer with game-exit guard, program/save backup, health checks and rollback. Seal and validate the release.
- [ ] If deployment is authorized and the game is exited, use only the unified deploy entry, then verify healthy server, preserved human data and NPC progression/ranking. Otherwise present the ready release and the specific remaining game-exit/authorization requirement.
