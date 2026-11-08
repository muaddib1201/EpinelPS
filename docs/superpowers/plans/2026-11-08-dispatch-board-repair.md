# Dispatch board repair implementation plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task by task.

**Goal:** Correct the dispatch reset response and repair malformed boards while preserving running tasks and earned rewards.

**Architecture:** Resolve each board level from its own academy lesson rewards. Normalize the remaining daily slots by dispatch type, accounting for completed and running tasks, and generate each category once. Get, reset and start/cancel use the existing player/save transaction so failed saves roll back.

**Tech Stack:** C#, .NET 10, protobuf, existing JSON save and regression executable workflow.

**Spec:** User requested repair after diagnosis of 18 returned tasks against a 10-task allowance; records are in `.nikke/dispatch-diagnosis/diagnosis.json`.

## Global constraints

- Use this session's worktree and isolated static data/save files only.
- Preserve running tasks, their timestamps, claimed history and other player progress.
- Normal capacity is the normal board maximum plus infrastructure bonus; special capacities are counted once and require their own lessons.
- Reset uses configured probabilities and price, returns balances and commits immediately.
- Use project integrate, sync, test, publish, seal and deploy entry points; never stop an active game to install.

## Review focus

- Already claimed slots must not be recreated by reads or resets.
- Academy changes and rollover must preserve unclaimed running tasks.
- Exhausted pools and insufficient currency must fail without partial mutations.
- Concurrent dispatch mutations must share the player lock.
- Legacy records must normalize without granting new progress or losing running tasks.

### Task 1: Regression and repair

Files: `tests/DispatchRegression/{DispatchRegression.csproj,Program.cs}`, dispatch handlers/helper, and `User.GetDispatchCount`.

- [ ] Add real-handler tests for the legacy 18-task fixture, independent lesson levels, specialty capacity, repeated reads, running/claimed preservation, rollover, weighted reroll, payment and rollback.
- [ ] Run `nikke_workflow.py test --worktree <this worktree> --suite DispatchRegression`; verify the expected failures.
- [ ] Implement board normalization and reset using existing transactional save support; synchronize start/cancel with those operations.
- [ ] Verify targeted regressions, commit, obtain a fresh code review, integrate and sync.
- [ ] Run all regression suites and publish the latest integration; prepare and seal an installer with backups, rollback and health checks.
- [ ] Deploy through the unified entry point when the game is closed and verify health and player-state preservation. Report client visual acceptance separately.
