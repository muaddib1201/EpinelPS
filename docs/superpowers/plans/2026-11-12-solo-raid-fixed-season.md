# Fixed Solo Raid Season Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this focused fix in the existing session worktree. Steps use checkbox syntax for tracking.

**Goal:** Report a stable season deadline and stop accepting solo raid play at that deadline.

**Architecture:** Add explicit season configuration with compatible defaults. Use it for both manager ID and protocol period; enforce the same window inside battle mutations and map rejection to `SoloRaidPeriodResult.Failure`.

**Tech Stack:** C#/.NET 10, Newtonsoft.Json, existing protobuf handler regression harness, project collaboration workflow.

**Spec:** `docs/superpowers/specs/2026-11-12-solo-raid-fixed-season-design.md`

## Global Constraints

- Work only in this session's registered source/workspace directories.
- Default manager 1000044; start 2026-11-12T03:00:00Z; exclusive end 2026-11-18T20:00:00Z.
- No live account test mutations, score resets, reward claims, or daily reset changes.
- Use integrate/sync/test/publish/seal/deploy; preserve upstream and push the complete integration history to the authorized Fork without force.

## Review Focus

- A battle already in progress at the cutoff must not change standings via damage or close.
- Expired fast battle must grant no repeatable or first-clear rewards.
- A preloaded future boss must not change the current raid ID or deadline.
- Start is inclusive and end is exclusive, using UTC regardless of host timezone.
- Existing configuration without the new property must still have a fixed default deadline after restart.

### Task 1: Fixed configuration and enforcement

**Files:** `EpinelPS/Utils/GameConfig.cs`, new `EpinelPS/LobbyServer/Soloraid/SoloRaidSeasonConfig.cs`, `SoloRaidHelper.cs`, and affected solo raid handlers. Tests: `tests/SoloRaidRankingRegression/Program.cs`.

- [ ] Add failing handler tests for fixed default timestamps, future/expired seasons, wrong raid IDs, unchanged records/rewards, and readable final standings.
- [ ] Run `nikke_workflow.py test --worktree <session source> --suite SoloRaidRankingRegression`; confirm behavior failures against the old rolling implementation.
- [ ] Implement the configuration, UTC window validation, fixed period/manager resolution, and mutation guards. Map window errors to protocol failure before saving.
- [ ] Add explicit boundary/date-offset checks and run the same suite, including its fresh-process saved-state verification.
- [ ] Commit the focused fix and its documentation.

### Task 2: Verification and delivery

- [ ] Request an independent whole-change review and address substantive findings with reproduction tests.
- [ ] Integrate and sync; run `test --suite all` and inspect results.
- [ ] Audit new tracked history for saves/keys/tokens; push exact integration ref to the Fork and verify `ls-remote`.
- [ ] Publish; prepare an installer under this session's `.nikke` with game checks, program/save backup, rollback, HTTP checks and read-only period/ranking probe.
- [ ] Seal and check the release. If the game is open, obtain the user's game-exit handoff with the finished package ready. Deploy only through the unified entry.
- [ ] Report actual deployment/backup status, official boss comparison, fixed local deadline and remaining client visual acceptance.
