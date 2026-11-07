# NPC Social Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Expand managed unions to five, make all NPCs actively develop across the existing five roster tiers, and persist the strongest thirty NPC friends with daily social gifts.

**Architecture:** Extend the current JSON state and managed NPC service with versioned one-time reconciliation. A focused friendship service owns mutual membership, daily ledgers and atomic gifts; authenticated controllers expose existing Protobuf contracts.

**Tech Stack:** C# / .NET 10, ASP.NET Core, Newtonsoft.Json, Google.Protobuf, EF Core SQLite.

**Spec:** ../specs/2026-11-07-npc-social-design.md

## Global Constraints

- Preserve all existing inventories, effective levels, campaign progress, IDs, paused status and guild memberships, including KUROINU.
- Five managed unions plus the user's union; new names OverClock and 貓咪補給站.
- Thirty mutual friends, sorted by current main-team combat, commander level then UID, initialized once for user 1552239268.
- All NPC activity tier 2; five existing SSR/skill tiers remain; game-day reset UTC 20:00; 30 sent/received gifts maximum, one point per claimed gift.
- No live mutation while the game runs; backup current files and verify source hashes before integration/deployment.

## Review Focus

- Replaying migration after deletion must not replace the user's chosen friends.
- Removing and re-adding a friend must not reset daily send/claim entitlement.
- Midnight reset and clock rollback must not grant the same entitlement twice.
- Save failure must restore both sides' friendship/gift state and currency.
- Concurrent gift operations and NPC snapshot replacement must retain committed progress.

### Task 1: Managed population reconciliation

**Files:** ManagedNpc model/service/factory/worker, admin model/view, new Services/ManagedNpcSocial.cs; tests/ManagedNpcRegression/SocialChecks.cs.

**Interfaces:** Produce `ManagedNpcService.UpgradeSocial(DateTime now)`; preserve `Update` signature while normalizing legacy activity input to tier 2.

- [x] Create an isolated snapshot of current dirty source and fixture data, record hashes, and run the current managed NPC baseline.
- [x] Add failing behavior checks: 3→5 unions, unique names, 8 members in new unions drawn only from unassigned NPCs, unchanged existing membership, 100 active tier-2 profiles across five roster tiers, and repeated upgrade no-op.
- [x] Implement versioned expansion and tier normalization; set factory defaults to tier 2, remove three-way activity UI, keep tier labels.
- [x] Run managed NPC default/restart/growth/breakthrough/team/social modes. Expected: all PASS.

### Task 2: Persistent friends and gifts

**Files:** new Models/Friendship.cs, UserModel.cs, new LobbyServer/Friend/FriendshipService.cs and LobbyServer/Controllers/FriendController.cs, existing GetFriends.cs; tests/FriendRegression.

**Interfaces:** Produce friendship `Get`, `Send`, `Obtain`, `All`, `Delete`, and one-time `AddStrongestNpcFriends(User user, DateTime now)`, with an injected timestamp at the service boundary for deterministic tests.

- [x] Write failing checks for top-30 mutual persistence and real Protobuf list data; one-time selection survives deletion and new rankings.
- [x] Implement user friendship state, transactional mutation with rollback, mutual friends and matching protocol replies.
- [x] Write failing gift tests for a single pair (first succeeds, second cannot credit), 30 distinct senders (balance +30 only), daily reset, backward time, remove/re-add, save failure, concurrent obtain and restart. Verify failures.
- [x] Implement gift ledgers and NPC daily gifts; queue actual FriendshipSend mission events in the JSON transaction and deliver using the existing outbox.
- [x] Run FriendRegression and restart mode. Expected: all PASS with unchanged user currency on initialization/read.

### Task 3: Review, integrate and install

**Files:** work-area progress/report/build/install/probe scripts, spec and plan.

**Interfaces:** Consume the source hashes, reconciliation methods and current authentication probe patterns.

- [x] Run the repository regression projects and relevant persisted modes from the isolated build. Expected: all passing, or identify baseline failures precisely.
- [x] Obtain a fresh review of the changed files and migration/gift edge cases, add RED→GREEN fixes for important findings.
- [x] Compare hashes before applying only changed files to shared source; refresh the candidate from latest shared source when needed so unrelated fixes are retained.
- [x] Publish a self-contained win-x64 candidate and prepare a guarded backup/rollback installer that waits for game exit.
- [x] Install when game is closed; verify HTTPS health, authenticated list has 30 expected friends, gift flags/counters, five managed unions, tier-2 defaults and preserved human/guild data. Do not spend live daily gifts in probes.
- [x] Save Chinese roster/result report and installation receipt; distinguish server verification from in-game acceptance.
