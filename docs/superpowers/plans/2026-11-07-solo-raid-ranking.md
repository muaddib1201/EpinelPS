# Solo Raid Ranking Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this repair task-by-task. The user already authorized the diagnosed repair.

**Goal:** Return actual personal solo raid rankings and retain the best completed challenge score.

**Architecture:** Complete the existing legacy protobuf endpoint/helper, using current-season saved Trial level 8 records and existing profile construction. Serialize ranking reads and battle mutations with the save lock. Retain legacy save compatibility.

**Tech Stack:** C# / .NET 10, protobuf, JSON save, SQLite profiles.

**Spec:** `docs/superpowers/specs/2026-11-07-solo-raid-ranking-design.md`

## Global Constraints

- No fabricated NPC scores, reward changes, new dependencies, or live account battle submissions.
- Preserve stacked live fixes; isolate build inputs from concurrent unfinished source work.
- The game must exit before backup/install/restart.

## Review Focus

- Old seasons, normal/practice stages, zero/negative scores and unfinished attempts must not enter ranking.
- A lower/equal five-team run must not destroy a previous best result or its team logs.
- Unranked self data must contain a valid profile and zero rank/damage.
- Ties and 64-bit scores must be deterministic and survive restart.
- Ranking queries must not reset attempts, mutate saves, or expose nonexistent reports.

### Task 1: Ranking and best-result repair

Files: modify `EpinelPS/LobbyServer/Soloraid/GetRanking.cs`, `SoloRaidHelper.cs`; add `tests/SoloRaidRankingRegression`.

- [x] Write and run failing endpoint and completed-attempt regression tests against verified live source.
- [x] Implement `SoloRaidHelper.GetSoloRaidRanking(User, ref ResGetSoloRaidRanking)` and wire `GetRanking`.
- [x] Preserve best completed Trial results and serialize relevant mutations.
- [x] Run targeted, restart, transport and release-compatible full regression checks; perform fresh code review.

### Task 2: Package and install

- [x] Publish the verified baseline plus only this repair and record source/binary hashes.
- [x] Once the game is closed, back up the running server and saves, install and restart.
- [x] Run authenticated read-only ranking probe and record installation receipt; distinguish server verification from game UI acceptance.

The user later confirmed client exit. The latest NPC social release had already incorporated this exact repair, installed it with a verified backup, and passed its combined 45-mode/392-group regression set. Both production repair source fingerprints match that release. A fresh authenticated live solo-raid probe passes, including the personal profile. Adoption receipt is in `.tools/solo-raid-ranking-20261107/installation.json`; do not install the superseded standalone package. Game UI acceptance remains pending.
