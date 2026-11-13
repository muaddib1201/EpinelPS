# Select-up Payment Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline; request one read-only whole-branch review before integration.

**Goal:** Make select-up ticket recruitment deliver the selected characters and deduct the configured resources atomically.
**Architecture:** Extend the existing ExecGacha payment pipeline and retain MissionProgressDelivery rollback.
**Tech Stack:** C# / .NET 10, protobuf handlers, JSON save and SQLite mission triggers.
**Spec:** `docs/superpowers/specs/2026-11-13-selectup-payment-design.md`

## Global Constraints

- Own registered worktree and synthetic data only; unified test/build/seal workflow.
- No changes to probabilities, live accounts or live service without deployment authorization.

## Review Focus

- Mixed tickets and gems must deduct only the missing draws, returning final balances.
- Invalid banner/ticket and discount combinations must leave all player state unchanged.
- Insufficient paid-gem fallback must roll back earlier deductions and generated rewards.
- Concurrent requests must not spend the same ticket balance twice.
- Success must persist selected characters, paid amounts and pull/mileage counters together.

## Task 1: Payment repair

- [ ] Replace the temporary `SelectupPaymentDiagnosis` project with `SelectupPaymentRegression`, asserting successful ticket recruitment for all three choices, counts 1/10 and correct persistence; add the failure/mixed/concurrency cases above.
- [ ] Commit tests and run `nikke_workflow.py test --worktree <session source> --suite SelectupPaymentRegression`; verify failure on unsupported currency before production edits.
- [ ] Extend `EpinelPS/LobbyServer/Gacha/ExecGacha.cs` to consume only the banner's configured select-up ticket and configured fallback resources. Reject invalid discounts and foreign tickets.
- [ ] Commit and run the new suite plus GachaRegression, DuplicateCoreRegression and ActivityMissionRegression.
- [ ] Request read-only review, resolve actionable findings, integrate and sync through the shared workflow.
- [ ] Run the full regression suite, publish, audit and push the exact integration SHA to the authorized fork, verify with ls-remote.
- [ ] Prepare a session-local installer with game-exit checks, program/save backups, rollback and health/save checks; seal and check the release. Ask for live stop/install/restart authorization only after the package is ready.
