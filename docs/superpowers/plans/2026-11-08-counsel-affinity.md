# Counseling affinity and repeat hints repair

**Goal:** Normal counseling uses the selected dialog's shipped 120/100 affinity value, quick counseling remains 100, and repeat-history responses reflect the player's collected entries.

**Architecture:** Resolve `ReqCharacterCounsel.SelectTid` only inside the requested character/conversation's `AttractiveCounselDialogTable` group before entering the existing user/save transaction. Keep `CounselMissionDelivery` as the atomic persistence, retry and mission-delivery boundary. `CheckCounsel` reports whether the authenticated user has collected the requested valid conversation. Keep journal/collection rewards unchanged.

**Constraints:** Preserve all existing local source changes, installed character language data, player progress, unlimited counseling policy, reward receipts and other server features. Unknown/missing/non-scoring choices retain the legacy 100 baseline and cannot obtain the 120 bonus. Do not invent historical bonus compensation. Back up before replacing the current live executable; require game exit and guard the installed program hash.

**Execution:** Use the current dirty detached checkout with before-file backups and a frozen candidate-source snapshot for publishing; a clean Git checkout would discard the stacked fixes. Implement inline using writing-plans, test-driven-development and verification-before-completion; one fresh read-only review before deployment. No unrelated Git staging/commits.

- [x] Add `CounselAffinityRegression`, using real shipped conversation rows and isolated JSON/SQLite fixtures. Observe 120 rewards and repeat-history checks fail on current code.
- [x] Resolve the selected row's affinity and apply it consistently to existing/new bonds, response gain and level carry. Keep quick counseling at 100. Restore truthful repeat-history lookup.
- [x] Verify 120/100/quick100, new/existing/capped bonds, level carry, reload, retries, failed saves, foreign choices/history/account isolation and unchanged collection state. Run the existing regression suites.
- [x] Obtain fresh code review, freeze/publish the current complete source, back up and install after game exit. Verify deployed behavior against disposable fixtures using the exact published assembly, authenticated read-only live history/journal and language mapping probes, executable hashes and human-account preservation.

**Review focus:** `selectTid` belonging to another group; startup/static data failure; stale references after rollback; level transitions and bond caps; retry receipts and unrelated collection rewards.

**Evidence:** `D:\Claude Lab\NIKKE\.tools\counsel-affinity-20261108\`.

## Verification ledger

- Initial regression fixture needed the matching SQLite GameUser rows for real mission notifications; fixed the fixture before evaluating behavior. Genuine RED: 5 passed, 8 failed. First implementation: 13 passed.
- Fresh read-only review found one Important race: a history query queued on an old user reference could observe collection state abandoned by a failed-save rollback. Added a deterministic real CounselMissionDelivery/queued-reader regression: 13 passed, 1 failed. Resolved current user again while holding user → SaveLock, then 14 passed. No Critical or Minor findings.
- The exact assembly produced by the final frozen-source publish also passed 14/14 in an independent fixture directory; its hash matches the assembly used for the packaged probe.
- Ruling: unknown/missing/non-scoring/foreign selection IDs keep the existing base100 policy and never gain120; this retains compatibility with current legacy callers and bounds this repair to the missing answer bonus.
- Ruling: preserve current unlimited counseling and existing collection semantics. No new attempt limits, retrospective affinity adjustments, or player-data migration.
- Initial full suite had two environment failures: FriendRegression's migration fixture read an already-seeded live snapshot, and the original-checkout LiberateResetRegression had no restored assets (earlier installations built it in a frozen tree). Final runner uses the pre-seeding snapshot only in its isolated Friend fixture and restores missing build assets. Production code is unchanged for both cases.
- Final full suite: 26 projects passed. Summary excludes the extra WeeklyActivity run because that project does not implement `--verify-saved`; its passing duplicate run remains in full-regression-runs.json. The meaningful final result is 41 runs / 395 passing groups, including the 14 affinity tests, with no failures.
- Deployment preflight caught one existing source change outside the repair: RewardUtils.cs now includes `Lv = newItem.Level` in an inventory reward reply. The exact final full-suite assemblies all share SHA256 BB2F4F1B83CC6ECDC78D04B48F2CFB56A9FED44E2DEFB84837AAB7897E067BEE and were built from this current workspace. Preserve the existing line and refresh the frozen publish source to match; do not weaken source-drift guards or revert another task's edit. Re-run the 14-case packaged-assembly probe after republishing.
- Final frozen build uses `--no-incremental` so a preserved source timestamp cannot hide changed contents. Final published executable SHA256 AEEFF8A0FAE7A31A744B474F58E22A7F267855DADD894EE0E7685351ACD4462F; exact packaged assembly passed 14/14 again. Preflight verified all 850 frozen production files and 402 probe/verification files.
- Installed after confirming no game process. Backup: `D:\Claude Lab\NIKKE\backup\server-20261108-004953-832-before-counsel-affinity`. Server PID 40876. Authenticated live repeat-history/journal checks, exact human-account comparison, and all 107 character names in four languages passed. Client +120 display and answer-hint visuals remain manual acceptance.
