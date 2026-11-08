# Lost Sector mission progress repair

The player has cleared Lost Sector 1 and claimed both its final chest and perfect exploration reward, but Day By Day still shows 0/1. SQLite has no LostSectorClear (53) trigger, and the final chest handler never writes one.

Restore the missing mission notification, once per user and sector. The existing ReceivedFinalReward flag for a known static sector is the authoritative completion evidence. Use the static sector ID as ConditionId and Value=1, checked against the actual Day By Day table in regression tests. An open field, partial combat progress, or a perfect-reward flag alone does not prove final-chest completion.

Reuse MissionProgressDelivery's persisted JSON outbox for crash recovery. Reconcile after final-chest save, at startup, and before trigger sync. Check both delivered and queued triggers before adding any. Recovery changes mission notifications only; it never pays rewards, edits event reward claims, or fabricates clears for other sectors. Existing final-chest reward behavior stays intact.

A one-time player database edit would fix this save but leave future clears broken. Adding only a new-clear notification would leave existing saves broken. Reconciliation plus the existing durable outbox covers both with one implementation.

Work only in this chat's registered worktree. Commit, integrate, sync, run all regression suites, publish, seal and deploy using the shared workflow. Installation checks the game has exited, backs up the program and both save stores, rolls back on failure, and verifies health and unchanged player progress/rewards. Client display still needs in-game acceptance after restart.
