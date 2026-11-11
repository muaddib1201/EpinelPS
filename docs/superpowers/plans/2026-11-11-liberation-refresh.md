# Liberation refresh repair implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline with test-driven-development; request a fresh code review before integration.

**Goal:** Return three valid liberation missions after refresh, repair the existing oversized list, and preserve the already claimed character, progress and rewards during deployment.

**Architecture:** Resolve the daily mission group from the liberation character and stage. Refresh only unfinished expired/empty batches; trim legacy oversized batches to the existing three client slots without changing their mission records. Completed records do not refresh or grant another final reward. Repair the authorized live list in a sealed installer while the server is stopped, with semantic save checks and rollback.

**Tech Stack:** C#/.NET 10, existing protobuf lobby handlers, isolated regression fixtures, project integration and deployment workflow.

**Spec:** The user approved fixing refresh and the existing list, preserving the claimed Quency, progress and rewards, then deploying. Prior analysis identified a 39-entry reset pool being returned to a three-slot UI.

## Global constraints

- Only the current registered session worktree may be edited, built or tested.
- No test may write live data. Live repair runs only through seal/deploy under the shared deployment lock.
- Preserve character inventory, liberation Step/ProgressPoint/IsCompleted/RewardedCount, wallet, claimed mission metadata and unrelated accounts.
- Push the verified integration commit and history to the authorized GitHub integration branch without force.
- Installation requires a game-exit check, verified program/save backup, rollback and HTTP/HTTPS health checks.

## Review focus

- Expiration after a paid refresh must use the current character/stage default group, never the reset pool.
- Completed liberation must keep claimed mission metadata on repeated reads, including expired timestamps.
- Legacy 39-entry and empty batches must not crash or erase progress; ordinary mixed daily/reset tasks remain valid.
- Save failure must restore the prior in-memory state; overlapping reads remain serialized by the player lock.
- Retried final rewards and missions for an already completed liberation must not grant anything again.

### Task 1: Repair and deploy liberation refresh

**Files:** `EpinelPS/LobbyServer/Liberate/{LiberateHelper,GetLiberateData,CompleteMission,ObtainStepReward}.cs`, `tests/LiberateResetRegression/Program.cs`; ignored session-owned maintenance/installer artifacts in `.nikke`.

- [ ] Add failing handler regressions for expired reset-pool tasks, completed oversized/expired lists, empty lists, save rollback and completed reward/mission retries.
- [ ] Run the regression through `nikke_workflow.py test --suite LiberateResetRegression`; confirm behavioral failures on the old implementation.
- [ ] Implement minimal canonical generation, conservative oversized-list normalization, rollback and completed-state guards. Run the targeted regression.
- [ ] Commit, request an independent review, address important findings, integrate and sync through the project workflow.
- [ ] Run `nikke_workflow.py test --suite all`, inspect results, scan the new commit history for sensitive files and push the exact integration commit; verify the remote SHA.
- [ ] Publish, prepare and review a new installer that changes only the authorized mission list alongside the tested program, seal and check-release, deploy, then verify the persisted full save and server health.

## Ledger

Pre-flight: no shared interfaces beyond the existing liberation protobuf handlers and mission records.
