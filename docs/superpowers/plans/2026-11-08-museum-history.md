# Museum history implementation plan

> Implement in this session using test-driven development and one final independent review.

**Goal:** Independent, explicitly labelled NPC museum history with complete damage and five-team details.
**Architecture:** A fixed embedded JSON catalogue is the shared source for ranking and squad responses. Existing personal best snapshots remain account scoped.
**Tech Stack:** C#, .NET 10, protobuf, existing JSON serializer.
**Spec:** `docs/superpowers/specs/2026-11-08-museum-history-design.md`

## Global constraints

- Use only this session worktree and its copied static/test data.
- Retain ranking mission targets and ordinary HP offset required by the client.
- No fictional human records and no live fixture writes.
- Publish and deploy through the collaboration workflow after integration.

## Review focus

- Every valid historical target returns exactly its five teams; invalid targets return empty.
- Ranking and squad damage agree using 64-bit arithmetic and the HP offset.
- History cannot change with viewer profile or owned roster changes.
- Current attempts cannot leak into saved personal best details.
- A packaged release must contain the archive and retain task/mission compatibility.

### Task 1: Archive and ranking/squad routes

Files: new `tests/SoloRaidMuseumRegression`, new `EpinelPS/LobbyServer/SoloraidMuseum/MuseumHistoryArchive.cs`, embedded `MuseumNpcHistory.json`; modify `EpinelPS.csproj`, `SoloRaidMuseumHelper.cs`, and `Handlers.cs`.

- [ ] Write handler-level tests for all above behaviors and inspect existing static stage/character data.
- [ ] Run `nikke_workflow.py test --worktree <session source> --suite SoloRaidMuseumRegression`; expect missing independent profiles and empty historical squads.
- [ ] Create fixed NPC records with five disjoint valid teams and exact damage splits; implement catalogue lookup and wire both routes to it.
- [ ] Calculate personal rank from the fixed challenge damage tiers; return only saved best team snapshots.
- [ ] Run targeted museum and solo raid regression suites; require zero failures.
- [ ] Commit only these files.

### Task 2: Integration, validation and deployment

- [ ] Independent review of changes, archive validity and package/deployment behavior; fix material findings with reproducing tests.
- [ ] Integrate and sync through `nikke_workflow.py`, run `--suite all`, and publish the latest clean integration commit.
- [ ] Prepare session-local installer with game-exit guard, program/save backups, rollback and startup checks; seal and verify the release.
- [ ] Deploy through the unified locked entry when the game has exited, then verify live museum responses and save preservation.
