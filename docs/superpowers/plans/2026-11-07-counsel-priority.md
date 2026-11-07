# Counsel priority implementation plan

> Execute inline with superpowers:executing-plans. This plan validates the server hypothesis before claiming the client behavior.

**Goal:** Prefer uncollected eligible normal counseling conversations, falling back to random when collected.

**Architecture:** The existing client selects a table ID then requests a boolean pre-check. Share a read-only decision between both existing check routes. Return true only for a completed selection while an eligible uncollected conversation exists for that account and character. The client's retry behavior is a required acceptance condition, still unverified.

**Tech stack:** .NET 10, existing protobuf dispatcher and static MemoryPack tables.

**Spec:** ../specs/2026-11-07-counsel-priority-design.md

## Global constraints

- Preserve current on-disk source modifications and all deployed saves.
- Never consume attempts, unlock conversations or grant rewards in a pre-check.
- Respect character, account and inclusive bond level ranges.
- A complete collection must not result in an endless rejection loop.
- Wait for game exit before any server replacement. Client acceptance is distinct from protocol tests.

## Review focus

Unknown IDs, duplicate journal entries, another account's progress, unavailable missing conversations, and stale or absent bond data must not block counseling.

## Tasks

- [x] Add CounselPriorityRegression using the real dispatcher and isolated saves; observe rejection tests fail against the current fixed-false response.
- [x] Load AttractiveCounselTable and implement the shared pre-check; verify both routes, all collection and eligibility cases, and no mutations.
- [x] Run existing counsel-reset, daily-mission and bond regressions; build from current source, review the focused diff, and stage a candidate release.
- [ ] Install only after game exit with backup and health checks. Verify real client selection/retry behavior; retain only an implementation that satisfies this condition.

## Execution rulings

- Work in the current patched checkout with file backups: detached HEAD and extensive pre-existing local changes make a clean HEAD worktree omit existing fixes. Do not stage or commit unrelated files.
- REA can list local devices, but attaching to the running client was refused by Windows (WriteProcessMemory access denied). No scripts were loaded and no session was created.
- Do not claim a client fix solely from a green service-side regression.
- Verification: RED 4 passed / 8 expected failures; GREEN 12 / 12. Existing CounselReset 7, DailyMission 15 + 3 restart, BondEpisode 6, ActivityMission 25 passed. Release build succeeded; 23 existing compiler warnings were reported on the full rebuild.
- Fresh read-only reviewer found no confirmed P1/P2 server defects. Minor untested cases remain: an existing but empty bond, selected-row expiry, and missing group data. Ruling: Lv <= 0 uses the existing level-1 default, not a blanket bypass; no real affected save was observed. These boundary tests are deferred, not evidence of a complete client feature.
- The NIKKE window access request timed out. User was asked whether to allow window access for actual testing or perform the client acceptance manually. Keep acceptance pending until a true-to-new-check sequence and actual story playback are observed.
- Candidate installation: deployed executable SHA256 `552E44E83B658EB476B0FF0EDDA18CBA698F667866185BECD074FEBAA1620054`; previous latest activity-mission release hash `92B23033F1E12F3AE694C0C16C1A69F01217F5A8DB4BAC627780E004E5AE6A4E` is backed up with consistent JSON/SQLite and configuration. Both authenticated check routes were verified against the real 19/20 collection; all human account JSON matched the pre-start snapshot. The remaining unchecked task is specifically the client retry/playback acceptance.

## Client rejection and revised investigation

- User manual acceptance failed. At 09:08:05, the client checked collected 3396 and received true; at 09:08:16 it completed queen_16 and counseled with 3396. This falsifies the retry interpretation. Do not ship or reuse the server hypothesis as a fix.
- Restored all three changed production source files to their pre-candidate content; removed the new helper. Moved this task's failed test project and preserved code into `.tools/counsel-priority-20261107/failed-server-candidate`. The patch filename now explicitly ends in `.failed-server-candidate.patch`.
- Prepared a one-time, hash-guarded withdrawal worker in `.tools/counsel-priority-20261107/withdraw-server-candidate`. It waits for game exit, backs up the current state, restores only the exact prior executable, preserves current gameplay progress, and verifies startup, human accounts and authenticated read-only responses. It must abort if another task replaces the program first.
- Static investigation recovered 180 CodeGenModules and CodeRegistration RVA 8AF8440, MetadataRegistration A0C1780, plus static metadata pointer slot B95EEE8. The metadata source file on disk is empty and the loader is protected; actual plaintext metadata has not been captured.
- Prepared `read-counsel-metadata.ps1`: build-hash bound, VM_READ/QUERY_LIMITED_INFORMATION only, no injection, memory writes, UI actions or account data reads. A normal-permission run failed OpenProcess with access denied.
- User authorized one administrator read-only diagnostic. The subsequent Windows RunAs launch returned operation-cancelled; no administrator diagnostic ran and no metadata was captured. Do not repeat the elevation under the consumed one-time authorization without new user approval.
- Prepared a bounded offline metadata indexer using version-specific table layouts and independently recovered registration counts. It has passed syntax verification only; it has not run against real captured metadata.
- Current goal is incomplete: a client-side selection change and actual repeat/missing/full-collection acceptance are still required.
- Withdrawal completed at 09:28:14 and preserved current progress. A separate NPC profile update then installed hash `FAC05F69DF05B2C056688FB3EE824F21E788FE3CE11329B9200C88A130751800`. Do not overwrite this newer program. The authenticated owner-context probe verified that both current check routes return their original flags and leave account data unchanged; HTTPS returned 200. Evidence: `withdraw-server-candidate/final-health.json` and `owner-probe.log`.
