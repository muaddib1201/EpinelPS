# Shooting Range Records Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for inline implementation. Preserve the existing modified checkout and use isolated test databases.

**Goal:** Persist shooting range scores and battle history and show real guild rankings.

**Architecture:** Add guild-scoped best scores and personal protobuf battle snapshots to the existing atomic JSON store. A focused service implements submission, history and ranking logic; MVC routes use the existing session and save lock conventions.

**Tech Stack:** C#, .NET 10, ASP.NET Core MVC, Google.Protobuf, Newtonsoft.Json, existing SQLite profile data.

**Spec:** `docs/superpowers/specs/2026-11-05-shooting-range-records-design.md`

## Global Constraints

- Preserve 64-bit scores and all existing account data and source modifications.
- Return fifteen regular element/length boards; retain fixed-spec battles only in history.
- Retain the latest 100 battle snapshots independently of best scores.
- Use the existing atomic save and session validation; never write test data to the live store.
- Install with the game closed and retain rollback backups.

## Review Focus

- Transport retry after restart must not duplicate a retained battle or change its receipt.
- Lower scores, equal scores and history pruning must not erase personal bests.
- Multiple guilds and changed memberships must not share ranking entries.
- Battle telemetry and element presets must produce immutable squad snapshots.
- Save failure and concurrent submissions must preserve history/leaderboard consistency.

## Tasks

### 1. Score, history and leaderboard flow

Files: create `tests/ShootingRangeRegression/{ShootingRangeRegression.csproj,Program.cs}`, `EpinelPS/Models/ShootingRangeModel.cs`, `EpinelPS/LobbyServer/Guild/ShootingRangeService.cs`, `EpinelPS/LobbyServer/Controllers/ShootingRangeController.cs`; modify `EpinelPS/Models/{DbModels,UserModel}.cs` and remove the placeholder route from `GuildController.cs`.

- [x] Add HTTP/protobuf regression tests for the stated review focus, authentication, invalid input, all categories, zero scores, fixed-spec practice and history bounds.
- [x] Run the regression executable and confirm submission/history fail because their routes are missing.
- [x] Implement `ShootingRangeService.Submit(User, ReqSetShootingRangeScoreV2, byte[]?)`, `Rankings(User, SavedGuild)` and `History(User)` with transactional rollback and immutable protobuf snapshots.
- [x] Run the new suite, then run it in a new process with `--verify-saved`; expect all tests to pass.

### 2. Compatibility and deployment

- [x] Run all existing regression executables with their normal and saved-state modes where supported.
- [x] Review the changed files independently; fix material findings with regression coverage.
- [x] Publish the complete current source to `.tools/shooting-range-records-20261105/publish`.
- [x] Back up the live executable and both stores, install with the game closed, verify HTTPS and all three unauthenticated routes, and confirm prior account data is unchanged.
- [x] Record verification results and deployment status. Real client acceptance remains a separate check.

## Execution Ledger

Ruling: Work in the current modified checkout because it contains the stacked production fixes. Build and test against separate disposable stores; do not create a clean checkout from HEAD that would omit those fixes.

Final review: two important findings reproduced by failing HTTP tests. Preserve encrypted batch identity through loopback dispatch with a distinct part index, and replay retained receipts before checking current guild membership. Submission now resolves guild membership inside the service after replay lookup.

Final review boundaries: synthetic support-character metadata now has coverage. Real-client rendering and precise unobserved client slot conventions still require gameplay acceptance; do not claim them from server tests.

Final: fixed batch retry identity and guild-change replay. Both reproduced as failing tests before the fixes; actual encrypted batch path and restart replay now pass. No deferred minor findings.

Verification: 20 shooting-range HTTP cases plus 2 new-process restart cases passed. All 10 existing regression executables and 4 supported saved-state runs passed (122 PASS groups). Release publish succeeded with existing unrelated compiler warnings.

Deployment: initial restricted-account startup could not load the original user-bound TLS PFX and automatically rolled the executable back. The original Windows user loaded the same certificate successfully, and rerunning the installer in that context succeeded at 2026-11-05 23:02:59 +08:00. Candidate SHA256 `EE4CDF9E92F07F76C3A61AAE87374E90D18E0E6A3EE8C1810CE11199EF0EED10`; backup `D:/Claude Lab/NIKKE/backup/server-20261105-230254-501-before-shooting-range-records`. HTTPS returned 200; all three anonymous routes returned 401; the real account could read all fifteen boards and its empty history, and invalid-stage submission returned 400. The full account save was deeply equal to its backup, and the authenticated probe left its file hash unchanged. No synthetic battles were written to the live account.
