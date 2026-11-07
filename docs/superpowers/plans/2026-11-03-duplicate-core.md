# Duplicate core correction implementation plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make duplicate acquisition reserve usable cores and pay Body Labels exactly once.
**Architecture:** One character reward helper, shared by existing acquisition paths; idempotent login reconciliation for existing excess.
**Tech Stack:** C#/.NET 10, protobuf, existing console regression harness pattern.
**Spec:** ../specs/2026-11-03-duplicate-core-design.md

## Global Constraints
- Preserve all existing uncommitted changes. Work in the supplied detached checkout, with file backups; do not stage or commit unrelated work.
- SSR/SR/R upgrade counts 10/2/0; compensation 6000/200/150 Body Labels. No co-op currency changes.
- Tests use isolated saves and SQLite, never live player data.

## Review Focus
- Actual character variant and saved Grade can disagree.
- Multiple inventory stacks and batch acquisitions can cross the cap.
- Unowned directly granted spare bodies must remain items.
- Response character serials, reward deltas, inventory and final currency must agree.
- Repeated login/daily-free requests must not award twice.

### Task 1: Reproduce acquisition defects
- [x] Add tests/DuplicateCoreRegression with real static character records, isolated SQLite/JsonDb, deterministic gacha fixtures and real handlers.
- [x] Run and record failures for maxed SSR/SR/R, mold stack preservation, mixed batch rewards, pity, daily-free and shop purchases.

### Task 2: Centralize grant behavior
- [x] Add Utils/CharacterRewardHelper.cs: GrantCharacter(User, CharacterRecord, int), TryGrantSpareBody(User, int, int, out NetRewardData), ConvertExistingExcess(User).
- [x] Route RewardUtils, ExecGacha, ExecutePityGacha, ExecuteEventGacha and UsePiece through it, preserving existing acquisition/messenger behavior.
- [x] Integrate spare-body and character grants in single/multiple shop purchases. Settle excess before EnterLobbyServer builds currency/items.
- [x] Run the new suite, all existing suites and a release build. Verify reconciliation persistence and repeated-login behavior.

### Task 3: Review and deploy
- [x] Get a fresh code review of this change and resolve material findings with regression coverage.
- [x] Publish from the current source, back up the running executable/configuration/saves, stop only this server, deploy executable/assets and restart hidden.
- [x] Verify process path, executable hash, listener/readiness and logs. Report game-client verification separately.


## Verification and delivery
- Initial 17/17 new scenarios reproduced failures before implementation.
- Final suites: DuplicateCoreRegression 24/24, CharacterToggleRegression 8/8, SupportCharacterRegression 20/20, TowerRegression 5/5 (57 total).
- Fresh-process persistence checks passed for duplicate compensation and character preferences.
- Review fixes: event-shop acquisition bypass, first unlock with unowned surplus, merged reward ownership, daily-free event validation, per-user synchronization for grants/reconciliation. Added reproductions for the four behavioral defects; concurrent reconciliation test also passes.
- Read-only audit of the real save found zero unusable spare bodies requiring conversion.
- Release publish succeeded; installed executable SHA-256 E0C764AFB258D86B9EEAC05A653D6C399FD00B777FC0623592C21135CA3EE5AD.
- Backup: D:/Claude Lab/NIKKE/backup/server-20261103-012352-before-duplicate-core.
- Restricted launch could not import the existing certificate. Normal-user launch succeeded; process 29816 listens on 80/443. Both HTTP and HTTPS admin checks returned 200; HTTPS verification used the normal Windows context because the restricted TLS client could not authenticate.
- Source and task-only patch retained in place. No unrelated local edits staged or committed. No multiplayer/co-op implementation was added. Actual game-client acquisition remains untested.
