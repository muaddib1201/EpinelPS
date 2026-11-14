# NPC Solo Raid daily routine

The user requested that existing managed NPCs participate in the active Solo Raid according to their own combat power and appear on its leaderboard. The existing five-phase routine settles other activities but never creates SoloRaidData.

## Approach

Integrate a separately checkpointed Solo Raid action after daily resource battles (phase 3). This also admits NPCs whose five phases have already completed today, and a season which opens later that day. Reuse the existing account records, reward registration and leaderboard; do not synthesize leaderboard rows or invoke the client battle engine. A standalone timer would duplicate scheduling and transaction ownership. Changing the leaderboard alone would create scores without progression or squads.

## Behavior and constraints

- Respect manager/NPC enablement, account bans, content unlocks, the confirmed season window, complete local presets and daily attempt limits. Read relevant limits and unlock settings from local game data, with documented conservative fallbacks if absent.
- Progress ordinary stages in order, awarding ordinary clear/first-clear rewards only for actual simulated clears. At most five squads of owned, distinct characters participate in a run; each character name may be used once per run. Stronger teams deal more estimated damage. Insufficient damage stops progression.
- Challenge unlock requires the ordinary stages and the preset's WaveOpenCondition prerequisite ID. The inspected local data supplies no additional challenge opening delay. Calculate challenge combat at the preset's fixed character level without modifying the account's levels, characters, skills, equipment or other saved teams. Use the existing CP formula and a documented deterministic damage estimate, consistent with existing NPC combat simulation.
- Persist challenge damage and original squad snapshots in SoloRaidData; retain the season's highest completed result and its squads. The existing ranking endpoint provides sorting, profiles and participant counts.
- Checkpoint per season/game day, including unsuccessful attempts. A repeated tick or restart cannot repeat attempts/rewards. No historical catchup. Preserve previous seasons and human accounts.
- All mutations and the checkpoint use the existing SettleDailyPhase rollback/save boundary; do not call nested helpers that save midway. A failed save rolls back records, resources and checkpoint, leaving a retry possible.
- Existing independent worktree, unified integration/testing/build/seal/deployment and authorized Fork backup rules apply. Prepare a reviewable release before any necessary game-exit handoff.

## Validation

Regression fixtures must exercise routine admission, own-power ordering, ordinary progression and rewards, attempt limits, challenge prerequisites/season window, unique owned squads, fixed-level calculations, best-result retention, pause/ban/locked/closed/missing-data skips, process restart and save-failure rollback. A population simulation should report when existing NPCs can first qualify; human saves and shared static tables must remain unchanged.

The local tables configure 3 ordinary attempts and 3 challenge attempts per day, at most 5 squads, a 180-second wave, and fixed level 400 for the current challenge. Damage is estimated as owned CP times 12 times wave seconds, multiplied by burst efficiency (20/cycle seconds, or 0.25 without a sustainable cycle) and seeded 95%-105% execution. Partial and SR/R squads use the same cadence check; primary roster selection retains its five-SSR requirement. Active season and stage validation is shared lazily across a worker tick.
