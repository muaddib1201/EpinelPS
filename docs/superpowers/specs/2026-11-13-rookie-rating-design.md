# Rookie arena rating changes

The requested change replaces the fixed +/-20 rookie arena settlement with a rating-gap-dependent calculation for humans and managed NPCs. Existing ratings, rewards, matching, special arena, and season resets stay unchanged.

## Evidence and approach

Public gameplay descriptions agree that rookie arena uses an Elo-like system and both attack and defense affect rating:
- https://www.prydwen.gg/nikke/guides/pvp-intro
- https://nikke.gg/arena-pvp/

Neither description establishes official coefficients, rounding, or bounds. The available ArenaRecord schema and previously exported ConfigGameTable also provide no verified rating coefficients. This change is therefore an explicitly documented approximation, not a claim to reproduce the official server exactly.

Keep fixed scoring (does not meet the request), invent a linear gap table (no evidence), or use conventional Elo. Choose Elo with K=40 and scale=400 to preserve the existing equal-rating +/-20 baseline while making unequal matches dynamic. These coefficients are implementation choices.

## Settlement contract

- Calculate the winner's gain once from both ratings immediately before settlement: `40 / (1 + 10^((winnerRating - loserRating) / 400))`.
- Round to the nearest integer, midpoint away from zero, then clamp the transfer to 1..40. This bound is a local approximation too.
- Apply that same transfer positively to the winner and negatively to the loser. Clamp each stored rating to 0..Int32.MaxValue using wide arithmetic; preserve the existing floor behavior even when it prevents exact conservation.
- Equal ratings transfer 20; a 1720 winner against 1800 gains 25; a 1800 winner against 1720 gains 15; a 400-point gap transfers 36 for an upset or 4 for the favorite.
- Use the shared FinishRookie path for human attacks, NPC attacks/defenses, and autonomous NPC routines.
- Return and persist the actual before/after ratings in both players' records. Existing receipt replay, transaction rollback, and duplicate prevention remain in force.
- No migration or recalculation of past matches. Activation affects subsequent successful settlements only.

## Verification and release

Exercise real HTTP settlement for both outcomes, score gaps, human/NPC combinations, numeric bounds, retries, saved receipts, and the autonomous NPC path. Run relevant arena and NPC regressions after integrating and synchronizing. Prepare a sealed release under the session worktree and back up the validated integration commit to the authorized Fork. Installation follows the project's unified deployment entry and current user authorization.
