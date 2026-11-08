# Alice diary progress repair

**Goal:** Restore already earned Alice skill and affinity diary progress and keep future changes counting.

**Design:** At trigger synchronization, reconcile owned Alice's actual skill 1, burst and bond levels with cumulative existing history. Append only missing positive differences in one SQLite transaction under the existing account synchronization gate. Do not claim rewards or change character levels. Character identity follows NameCode 5004 across breakthrough IDs.

**Authorization:** The user requested both retrospective repair and correct future counting. Use this chat's worktree and the project's integrate/test/publish/seal/deploy entry points. Deploy only with the game exited, with save/program backups and rollback.

- [ ] Add endpoint regression tests; commit and observe the existing failure through the workflow test entry point.
- [ ] Add the small reconciliation service and call it from trigger sync; verify real diary definitions, retry/concurrency, existing partial history, skill routes, counseling and gifts.
- [ ] Review, commit, integrate, sync and run all regression suites; build the current integration revision.
- [ ] Seal a reviewed installer and packaged protocol probe. Back up, deploy, reconcile the authorized player's history and verify repeated incremental sync and save preservation under the deployment lock.
- [ ] Report server evidence and leave visual game confirmation explicit.
