# Lost Sector Mission Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this bounded repair in the current session.

**Goal:** Restore the missing Day By Day Lost Sector 1 progress and record future proven clears once.

**Architecture:** Reconcile authoritative final-chest flags against delivered and pending LostSectorClear notifications. Reuse the existing mission outbox instead of editing rewards or claiming event missions.

**Tech Stack:** C#/.NET 10, SQLite/EF Core, JSON saves, protobuf regression harness, project deployment workflow.

**Spec:** `docs/superpowers/specs/2026-11-08-lost-sector-mission-design.md`

## Global Constraints

- Source and test outputs belong to this chat's registered worktree.
- Only known sectors with ReceivedFinalReward qualify; no rewards or claim history changes during recovery.
- One notification per user and sector, including repeated sync, startup and queued delivery retries.
- Build and deployment use the latest integration commit and sealed evidence; never bypass the running-game check.

## Review Focus

- Partial exploration and unknown sector IDs must not gain progress.
- Existing notifications and queued delivery retries must not produce duplicates.
- JSON save failure must not leak an uncommitted notification.
- SQLite delivery failure must retain a recoverable persisted outbox.
- New clears must satisfy the real static Day By Day condition without changing original rewards.

### Task 1: Repair mission delivery and verify installation

**Files:** Add `EpinelPS/LobbyServer/Lostsector/LostSectorMissionProgress.cs`; update `ObtainItem.cs`, `Mission/MissionProgressDelivery.cs`, `Trigger/TriggerSync.cs`; extend `tests/LostSectorRewardRegression/Program.cs` and add a read-only live verification helper if needed.

**Interfaces:** `internal static void LostSectorMissionProgress.Recover(User user)` reconciles qualifying sector records under user/save locks and queues missing `Trigger.LostSectorClear` entries using `MissionProgressDelivery.Apply`. Existing startup `RecoverAll` and trigger sync invoke it.

- [ ] Write behavioral regressions for actual final-chest delivery, old cleared saves, repeat/startup delivery, incomplete/unknown sectors, queued retry and save failure. Check static mission trigger/condition and reward preservation.
- [ ] Commit only tests/docs and run `nikke_workflow.py test --worktree <this sourceDirectory> --suite LostSectorRewardRegression`; observe the missing mission notification failure.
- [ ] Implement reconciliation and its three call sites; run the focused suite, including fresh-process persistence.
- [ ] Commit, get a fresh code review, integrate and sync. Run `test --suite all`; fix material findings before claiming completion.
- [ ] Publish the latest integrated commit; write an installer in this worktree's `.nikke`, seal and check-release. Ask for game exit only when the package is ready if it is still running.
- [ ] Deploy through the shared entry, verify the one clear record and preserved rewards/progress, report the remaining client acceptance step.
