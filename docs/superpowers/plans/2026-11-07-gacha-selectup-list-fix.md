# Selectup character-list mapping repair

**Goal:** Resolve the character list from the current user's saved selectup choice when its GachaGradeProbId matches the rolled grade. Preserve rarity weights and shared game records.

**Approved scope:** The user requested repair after the audit reproduced empty-list failures on 10094, 10056 and 2001 and demonstrated their correct per-choice mappings in an isolated process.

**Implementation:** Modify only GachaUtils.SelectRandomCharacter's list resolution; guard the choice's banner ID and grade ID, and leave other grades on their original list. Add GachaRegression using the real cached static tables.

- [x] Write forced-branch regression tests and observe the original empty-list failures.
- [x] Resolve the selected list in a local variable; verify every configured mapping, alternating users, other selectup grades, official/custom rates, all banner definitions, and tutorial behavior.
- [x] Review the patch and run the existing regression programs against the final source.
- [x] Publish to a separate staging directory, record source and executable hashes, and prepare a backup/rollback installer.
- [x] After client exit, verified the running integrated release already contains the exact patch; all seven live no-reward simulations passed. The earlier staged executable was superseded, so no additional replacement or restart was needed.

The workspace contains stacked, uncommitted repairs. Keep them intact; save the changed file's baseline and guard the tested source with hashes. Deployment must abort if the game is running or the live executable differs from its recorded baseline.
