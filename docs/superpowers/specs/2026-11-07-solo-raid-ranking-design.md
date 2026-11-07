# Personal solo raid ranking repair

The user authorized fixing the diagnosed empty `/soloraid/getranking` response.

Use saved, completed, positive-damage Trial level 8 records for the active raid ID. Return one best result per eligible account, damage descending and account ID ascending for deterministic ties, plus the requesting player's profile, rank, damage, and the actual participant count. Unranked players receive rank/damage zero with a complete profile. Normal stages, practice, unfinished attempts, old raid IDs, and banned accounts do not contribute. Do not synthesize NPC scores.

Approach: implement the existing helper and wire the existing authenticated protobuf handler. No new storage or dependency is required. Preserve the existing best challenge result when a later five-team attempt scores lower; otherwise a working leaderboard would regress on subsequent attempts. Use the existing save lock for ranking and the battle mutations it reads.

Keep battle report IDs unset because the saved raid records do not contain report IDs or report timestamps. Battle report/replay implementation and NPC raid simulation are outside this repair.

Validate empty state, filtering, sorting, profiles, 64-bit damage, ties, repeat attempts, five-team completion, close/commit, read-only queries, authenticated transport, and restart persistence in an isolated test database. Build from the verified current live release plus this repair so concurrently unfinished work is not deployed. Back up and install only after the game exits, then run a read-only authenticated probe.
