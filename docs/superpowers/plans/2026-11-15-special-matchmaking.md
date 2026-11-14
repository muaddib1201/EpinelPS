# Special Arena Upward Matching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Offer only higher battle ranks in special arena and deploy a verified integrated build.

**Architecture:** Use TierTable ranges to build three higher occupied tier groups, rotate within groups, and validate new starts before consuming attempts. Reuse the groups for NPC special battles.

**Tech Stack:** C#/.NET 10, existing ArenaRegression HTTP fixture, PowerShell installer and nikke_workflow.py.

**Spec:** docs/superpowers/specs/2026-11-15-special-matchmaking-design.md

## Global Constraints

- Current session sourceDirectory only; isolated fixtures and output inside its workspaceDirectory.
- No saves, keys or credentials in source commits; preserve all latest integration changes.
- Special rivals must have positive SpecialRank strictly smaller than the viewer. At most three distinct legal rivals, one per higher occupied tier, best rank first.
- Use actual TierTable ordering, including non-contiguous Priority values. Sparse same-tier fallback remains strictly upward.
- Existing RookieRefresh path, points/rewards, save schema and pending-battle replay stay intact.
- No new start against a lower or invalid rank may consume attempts or tickets. NPCs keep the existing no-human-target policy.
- Latest integration only for publish; deploy only sealed manifests under the global lock after game exit.

## Review Focus

- First three positions, sparse ladders and absent higher tiers must never offer lower targets.
- Same-tier rotation must cover every eligible member without NPC/CP bias.
- Invalid defenses, banned and locked participants remain excluded.
- Rank changes before start and during a pending battle must preserve attempts and replay semantics.
- NPC top rank, legacy cursors and rookie mode isolation must remain safe.

### Task 1: Test and implement upward matching

**Files:** EpinelPS/LobbyServer/Arena/ArenaMatchmaking.cs, ArenaService.cs, ArenaBattles.cs, ArenaNpcRoutine.cs; tests/ArenaRegression/SpecialMatchmakingChecks.cs and Program.cs.

**Interfaces:** private User[][] SpecialRivalGroups(); private IEnumerable<User> SpecialRivals(); existing arena/special/getenemylist and start responses, existing SpecialRefresh.

- [ ] Commit HTTP and NPC regressions and run ArenaRegression on the old matcher; confirm intended failures.
- [ ] Implement tier grouping/rotation, start check after pending replay but before charging, and shared NPC candidate groups; commit and run ArenaRegression, ArenaCubeRegression and NpcDailyRoutineRegression.
- [ ] Request one independent review, address material findings with failing-then-passing tests, and record evidence.

### Task 2: Integrate and deploy

- [ ] Integrate/sync; verify ArenaRegression, ArenaCubeRegression, NpcDailyRoutineRegression and EquipmentExchangeRegression on the final integrated source.
- [ ] Inspect newly pushed history for saves/secrets; push the exact integration commit to the authorized Fork branch and verify with ls-remote.
- [ ] Publish, create and verify a fresh session installer, seal and check-release. Check actual-save special candidates in an isolated snapshot.
- [ ] After game exit, deploy through the unified entry; verify program hash, HTTP/TLS, JSON and SQLite preservation. Record backup and pending client acceptance.
