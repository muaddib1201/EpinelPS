# Ranking firsts and tower repair implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct tower ranking display and, as explicitly requested, assign existing first-achievement records to account 1552239268 (ZOLRATH) and advance its four manufacturer towers through floor 27 with normal missing-floor rewards.

**Architecture:** Tower ranking scores are floor numbers, while stage IDs remain only in clear triggers and static target definitions. A workspace-only maintenance tool runs on a stopped-save copy, uses the existing tower skip/reward implementation, settles normal milestones, transfers first records, and settles cumulative rewards. Only the unified sealed deployment installs the resulting saves and program.

**Tech Stack:** C#/.NET 10, existing protobuf endpoint regressions, SQLite/JSON, PowerShell unified deployment.

**Spec:** User request dated 2026-11-09 and its three screenshots. Existing achieved targets only; do not invent future targets or change live company growth scores. Corporate floor 27 means cleared through 27, with 28 next. Preserve Tribe Tower progress (currently 95), claimed ranking gifts, existing mail receipts, and unrelated account/NPC progression.

## Global constraints

- Use sourceDirectory returned for this chat; baseline 645616f598962bf870617f6569fd68e6d214c87b.
- Tests and maintenance staging remain inside this chat's workspace.
- No live writes or authenticated settlement probes outside sealed deploy holding the shared lock.
- Game must be exited at installation. Previous user authorization to test, deploy and restart continues.
- Preserve reward receipts; repeated maintenance must award no second set of floor or cumulative rewards.

## Review focus

- Floor versus stage ID at overview, detail, milestone threshold, and historical achievement boundaries.
- SQLite/pending clear history ahead of JSON must not duplicate rewards; other user/category history cannot count.
- Missing account, missing target/reward data, or invalid rank data must fail before installation.
- Rank reassignment must preserve unique positions and receipts, credit only new cumulative tiers, and remain stable after restart.
- A failed offline repair or post-install check must restore the complete stopped backup without corrupting WAL/SHM.

### Task 1: Tower protocol regression and fix

Files: EpinelPS/LobbyServer/Ranking/RankingService.cs; tests/RankingRegression/Program.cs.

- [x] RED: existing five-tower and durable-clear tests expect literal floors; add legacy achievement display regression. Run RankingRegression through unified test.
- [x] GREEN: capture floor.Floor, compare achieved floor to target static floor, convert legacy stage-ID achievement scores only at response boundary. Verify targeted regression, including restart and milestone rewards.
- [x] Commit the verified source fix.

### Task 2: Workspace-only repair utility

Files: EpinelPS/Services/RankingTowerRepair.cs; tools/RankingTowerRepair/{RankingTowerRepair.csproj,Program.cs}; tests/RankingTowerRepairRegression/{RankingTowerRepairRegression.csproj,Program.cs}.

- [x] RED: test reassignment swaps, missing self in top ten, receipt preservation, native reward amounts for missing manufacturer floors, durable/pending history deduplication, no Tribe change, malformed-input preflight, restart and repeated invocation.
- [x] GREEN: preflight all four towers, reconcile valid progress/history, invoke normal skip/reward and clear-trigger writers; settle normal new milestones; transfer current first records and send only newly gained tier events; settle cumulative mail. Fail CLI unless executable data directory is inside explicit workspace.
- [x] Verify targeted suites and commit. Review whole branch independently before integration.

### Task 3: Integration, full verification and deployment

- [ ] Integrate/sync; run all regression projects on latest integration; publish.
- [ ] Create a new installer in .nikke. Check game exit, stop, back up/hash all program/save files, run utility only against private staging, verify exact expected reward/progress/rank changes and unrelated data preservation, then replace/start/probe. Reuse reviewed rollback structure, not an old execution entry.
- [ ] Verify installer syntax and actual rollback branches; independent installer review; seal/check/deploy with current generation. Elevated unified entry may be needed to stop the existing server.
- [ ] Verify all existing first-achievement responses belong to ZOLRATH; tower overview/detail floors are 95 and 27/27/27/27, rewards match the staged ledger, repeated refresh does not grant rewards, HTTP/HTTPS health passes. Report client acceptance separately.

## Evidence ledger

- Read-only live inspection: .nikke/tower-first-repair-readonly-before.json. ZOLRATH TowerProgress is ALL=95, ELYSION/MISSILIS/TETRA/OVERSPEC=3. All 38 currently achieved targets include a ZOLRATH record; 23 firsts already belong to it and 15 belong to three NPCs.
- Root cause: RankingService.Capture sets scores[5+index] to floor.Id and tests explicitly assert those IDs. The screenshot displays the numeric score directly as a floor. Source correction must also update Reached, which currently looks that score up as a stage ID.
- Tower RED 20261109-001311-822172: 29 passed / 4 failed; GREEN real-floor display, old history, milestone and fresh-process checks passed.
- Maintenance RED 20261109-001727-390039: 0 passed / 9 failed for absent maintenance operation. Added real reward failure injection; atomic rollback RED 20261109-002423-615899: 9 passed / 1 failed. Existing MissionProgressDelivery.Apply now wraps the four-tower batch.
- Independent review found two P2s: direct tier-event delivery before first-record durability, and report/input collision. Real SQLite second-tier abort reproduced the first (20261109-003137-982459); real private CLI invocation with --report=db.json reproduced the second (20261109-003402-401519). Both fixed and independently re-reviewed at d725a7c with no P0-P2 remaining.
- Final targeted at d725a7c: 13 maintenance + 33 ranking + 5 tower groups passed, including second-tier outbox recovery, CLI collision rejection, failed reward rollback and fresh-process no-duplicate behavior.
- Read-only live snapshot preview .nikke/repair-preview-20261109-002758: 96 paid floors, 15 first transfers, all 38 existing firsts become ZOLRATH. Native table rewards total 5800 gems, 823600 gold, 300 core dust; UserExp 15356 advances commander 26 to 27 and grants the normal 30 level-up gems. Four existing manufacturer mold items gain 44 each; three additional mold items gain 5 each. Only requested user fields and three previous-first NPC Ranking states change. This preview is not a live installation.
