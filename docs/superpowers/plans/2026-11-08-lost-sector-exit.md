# Lost Sector reward level repair

> **For agentic workers:** Use superpowers:executing-plans for this focused repair.

**Goal:** Prevent the client from receiving a level-zero Harmony Cube immediately after collecting the Lost Sector 1 final chest.

**Evidence/spec:** The 2026-11-08 00:17:16 server response grants cube 1000301 in `Reward.UserItems` without `Lv`, while its saved `DbItemData.Level` is 1. Client Player.log at 00:17:21 and 00:17:23 (UTC+8) throws in `HarmonyCubeUpgradeData..ctor` and `BadgeContentHarmonyCube.IsUpgradable`, first in the reward popup and then while returning to LostSector. The protocol has no separate HarmonyCubes field in NetRewardData: its UserItems entry must carry Lv.

**Architecture:** Add the saved item's level to the existing generic item reward projection in `RewardUtils.AddSingleObject`. Keep reward amounts, creation, persistence, and Lost Sector state unchanged. Use the current complete source snapshot because the detached shared checkout contains installed fixes.

**Tech stack:** .NET 10, shipped static pack, real protobuf/HTTP handlers, isolated JSON/SQLite fixtures.

## Task 1: Reproduce and repair the reward response

- [ ] Add `tests/LostSectorRewardRegression` and demonstrate the new cube response decodes with level 0 although the saved item is level 1.
- [ ] Add `Lv = newItem.Level` to the generic new-item reward projection in `EpinelPS/Utils/RewardUtils.cs`.
- [ ] Verify actual Lost Sector 1 final chest payout, valid shipped level lookup, wire round trip, inventory agreement, existing item stacking, and fresh-process persistence.
- [ ] Run the existing regression suites, inspect the focused diff, and publish the complete verified current source.
- [ ] Integrate only the focused files after checking shared-source fingerprints. Back up and replace the executable while the game is closed; retain the live save and locale override.
- [ ] Verify server health and read-only live inventory. Record that actual in-game exit acceptance is pending.

## Constraints and review

- Do not change IsPlaying, exploration completion, cleared stages, reward flags, or live item counts.
- Do not reissue the already received final chest or attempt a live mutation as a probe.
- Equipment remains on its existing reward branch; stackable item levels remain 0.
- A successful server regression proves the missing wire field is corrected; it does not prove the game UI has been retested.
