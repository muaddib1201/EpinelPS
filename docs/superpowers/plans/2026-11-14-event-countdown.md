# MARIA-n Countdown Implementation Plan

> Execution: superpowers:executing-plans in the current session; one independent final review.

**Goal:** Send the real MARIA-n deadline rather than a rolling thirty-day date, then deploy and back up the verified result.
**Architecture:** EventHelper resolves a known fixed UTC deadline for event 83500 and its direct ParentsEventId/SetField children. Its list, joined and daily-event paths share the resolver; other events keep existing behavior.
**Tech stack:** C#, .NET 10, protobuf, SQLite synthetic fixtures, PowerShell and nikke_workflow.py.
**Spec:** ../specs/2026-11-14-event-countdown-design.md

## Constraints and review focus

- Work only in this session's registered source/workspace directories.
- Known deadline: 2026-11-18T19:59:59Z; do not change starts, saved claims or daily reset.
- Check both parent link fields, both client responses, unknown events, repeated reads and UTC conversion.
- No live writes during tests. Install only through sealed deploy under its lock; game must be exited.

## Task 1: Countdown regression and repair

Files: tests/EventScheduleRegression/{EventScheduleRegression.csproj,Program.cs,LiveChecks.cs}, EpinelPS/LobbyServer/Event/EventHelper.cs.

- [ ] Add a regression project using the real event tables and handlers. Assert event IDs 83500,40124,40125,60124,100053,83501,83502,83503 all send November 18 19:59:59 UTC in list and joined responses; screenshot remaining time is 4d 4h 53m 20s. Check repeat stability, synthetic unrelated events and unchanged claimed state/reset settings.
- [ ] Commit the reproducer and run `nikke_workflow.py test --worktree <this source> --suite EventScheduleRegression`. Expected: deadline assertions fail against rolling thirty-day code.
- [ ] Add `EventHelper.GetEventEnd(int eventId, DateTime nowUtc)` and use it consistently for end/disable defaults and daily event creation. Known root/direct children use the fixed date; unknown records return nowUtc.AddDays(30).
- [ ] Run the regression plus EventShopRegression and LiberateResetRegression. Expected: all pass. Commit the repair and record RED/GREEN evidence in .nikke/event-countdown/ledger.md.

## Task 2: Integration, release and backup

- [ ] Independent final review against the spec; address material findings with regression evidence.
- [ ] Integrate, sync and run `test --suite all`; expected every suite/mode exits zero. Publish latest clean integration.
- [ ] Prepare .nikke/install-event-countdown.ps1 with exit guard, verified stopped-server backup, rollback, startup health and read-only protocol probes. Seal and check-release; expected accepted.
- [ ] Once the game is exited, deploy the sealed manifest. Expected new program hash, HTTP/HTTPS 200, both event endpoints report the fixed date, human saves and participants preserved.
- [ ] Inspect new history for saves/secrets; push exact integration to muaddib1201/EpinelPS nikke/integration without force. Verify ls-remote and report actual deployment/backup evidence and pending client visual acceptance.
