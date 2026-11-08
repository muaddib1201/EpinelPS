# Collection composition repair

> Execute inline using the existing session worktree, TDD, and the project workflow.

**Goal:** Restore `/favoriteitem/compose`, which currently returns EmptyHandler responses, using this client's static-data rules.

**Architecture:** A focused FavoriteItem handler validates selected instance IDs, resolves homogeneous collection rarity, reads `FavoriteItem{R,SR}ComposeNeedCount` and `FavoriteItem{R,SR}ComposeReward`, awards the configured rewards and persists the change under the existing user and save locks. Rejected requests change nothing; a failed save restores inventory in place.

**Tech stack:** C#/.NET 10, existing protobuf messages, GameData, JsonDb and reward utilities.

**Spec:** User's confirmed missing-interface bug and request to repair it; `ConfigGameTable` sets R=4 and SR=3. No grants or repairs to the live player's inventory are required.

## Constraints and review focus

- All source, tests and evidence stay in this session's registered worktree; use the unified integrate/sync/test/publish/seal/deploy workflow.
- Reject duplicate, foreign, missing, equipped, enhanced, mixed-rarity and invalid collection IDs before any mutation.
- Support multiple full groups in one client request; draw each configured reward independently.
- Prevent concurrent/retried use of the same materials and preserve unrelated player state.
- Use both client reward representations and unique instance IDs; persist results through reload.
- Validate configuration before removal and restore existing object identities and counts on save failure.
- Deploy only with the game exited, backups, rollback and health verification.

## Tasks

1. [ ] Add `tests/CollectionComposeRegression` with synthetic players and real dispatcher requests. Run via `nikke_workflow.py test --suite CollectionComposeRegression`; observe the valid R request fail with missing reward. Inspect the loaded rule/reward records to settle implementation details.
2. [ ] Add behavior tests covering R/SR rewards, batch sizes, rejection cases, retry/concurrency, reload and failed persistence. Implement `EpinelPS/LobbyServer/FavoriteItem/ComposeCollectionItem.cs` using the existing configured reward definitions. Run the targeted suite until green, then review the diff and commit.
3. [ ] Integrate, sync and run all regression suites. Publish the integrated source. Prepare and seal `.nikke/install.ps1`, validate the sealed release and deploy under the shared lock when the game has exited. Verify health and preservation of the current live save; retain evidence in `.nikke`.
