# NPC Solo Raid daily routine

The user requested that existing managed NPCs participate in the active Solo Raid according to their own combat power and appear on its leaderboard. The existing five-phase routine settles other activities but never creates SoloRaidData.

## Approach

Integrate a separately checkpointed Solo Raid action after daily resource battles (phase 3). This also admits NPCs whose five phases have already completed today, and a season which opens later that day. Reuse the existing account records, reward registration and leaderboard; do not synthesize leaderboard rows or invoke the client battle engine. A standalone timer would duplicate scheduling and transaction ownership. Changing the leaderboard alone would create scores without progression or squads.

## Behavior and constraints

- Respect manager/NPC enablement, account bans, content unlocks, the confirmed season window, complete local presets and daily attempt limits. Read relevant limits and unlock settings from local game data, with documented conservative fallbacks if absent.
- Progress ordinary stages in order, awarding ordinary clear/first-clear rewards only for actual simulated clears. At most five squads of owned, distinct characters participate in a run; each character name may be used once per run. Stronger teams deal more estimated damage. Insufficient damage stops progression.
- Challenge unlock requires the ordinary stages and the configured opening delay. Calculate challenge combat at the preset's fixed character level without modifying the account's levels, characters, skills, equipment or other saved teams. Use the existing CP formula and a documented deterministic damage estimate, consistent with existing NPC combat simulation.
- Persist challenge damage and original squad snapshots in SoloRaidData; retain the season's highest completed result and its squads. The existing ranking endpoint provides sorting, profiles and participant counts.
- Checkpoint per season/game day, including unsuccessful attempts. A repeated tick or restart cannot repeat attempts/rewards. No historical catchup. Preserve previous seasons and human accounts.
- All mutations and the checkpoint use the existing SettleDailyPhase rollback/save boundary; do not call nested helpers that save midway. A failed save rolls back records, resources and checkpoint, leaving a retry possible.
- Existing independent worktree, unified integration/testing/build/seal/deployment and authorized Fork backup rules apply. Prepare a reviewable release before any necessary game-exit handoff.

## Validation

Regression fixtures must exercise routine admission, own-power ordering, ordinary progression and rewards, attempt limits, challenge unlock/delay, unique owned squads, fixed-level calculations, best-result retention, pause/ban/locked/closed/missing-data skips, process restart and save-failure rollback. A population simulation should report when existing NPCs can first qualify; human saves and shared static tables must remain unchanged.
