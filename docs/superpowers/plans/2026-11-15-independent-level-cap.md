# Independent SSR Level Cap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reject standalone SSR upgrades above 160 before MLB and above 200 after MLB without charging or changing saved state.

**Architecture:** One shared template-based validator protects both ordinary and batch character upgrades. Batch requests complete validation before any character is mutated. Synchro Device enhancement remains separate.

**Tech Stack:** C# / .NET 10, protobuf handlers, synthetic JSON and SQLite integration tests.

**Spec:** `docs/superpowers/specs/2026-11-15-independent-level-cap-design.md`.

## Global Constraints

- All source work and test outputs stay in this registered session workspace.
- SSR zero to two limit breaks: maximum 160. Three or more: maximum 200.
- Rejected requests must not consume materials or modify memory, JSON saves or SQL triggers.
- Use `nikke_workflow.py` for testing, integration and synchronization; push the verified integration commit without force. Do not deploy.

## Review Focus

- Stale redundant saved grades must not bypass a template's actual cap.
- A later excessive batch entry must not leave an earlier valid entry paid or upgraded.
- Duplicate entries and targets beyond the upgrade table must be validated before clamping or mutation.
- An unknown owned-character template must fail without charging materials.
- Legal cap-boundary upgrades, valid multi-character batches and enhanced device levels above 200 must remain usable.

### Task 1: Enforce standalone caps with request-level regression coverage

**Files:** Create `EpinelPS/LobbyServer/Character/IndependentLevelLimit.cs`; modify `LevelUp.cs`, `SynchroOneClick.cs` and `tests/CharacterLevelCapAudit/Program.cs`.

**Interfaces:** `IndependentLevelLimit.Validate(CharacterModel character, int targetLevel)` throws HTTP 400 for an unknown template or an excessive SSR target and performs no mutation.

- [ ] Extend the real-handler audit to assert HTTP 400 and unchanged user/save/trigger state for invalid ordinary and batch requests, plus the review-focus cases above.
- [ ] Commit the test-only change so the required workflow can run; run `test --worktree <session sourceDirectory> --suite CharacterLevelCapAudit`. Expected: functional failures for excessive targets and batch atomicity against the unchanged handlers.
- [ ] Add the shared validator and call it before any ordinary mutation and before the entire batch mutation loop. Keep the existing cost and enhanced-device code intact.
- [ ] Commit and run `test --worktree <session sourceDirectory> --suite CharacterLevelCapAudit DailyMissionRegression SynchroTutorialRegression`. Expected: all named suites pass.
- [ ] Obtain an independent whole-change code review; address material findings with regression evidence.
- [ ] Run `integrate`, `sync`, and `test --worktree <session sourceDirectory> --suite all`. Expected: the complete synchronized integration passes.
- [ ] Inspect newly pushed history for saves or secrets; push the exact verified `nikke/integration` commit to the authorized Fork and verify it with `ls-remote`.

Execution evidence and decisions are recorded under the session's ignored `.nikke` directory so verification hashes remain stable.
