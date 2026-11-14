# Independent SSR level caps

The user requested server-side limits for standalone SSR leveling: limit breaks 0–2 permit at most level 160; limit breaks 3 and above permit at most level 200. Both `/character/levelup` and `/character/synchrodevice/oneclick` must reject excessive targets before consuming materials or modifying saved state.

## Approach

Use one shared validator in `LobbyServer/Character/IndependentLevelLimit.cs`. Resolve rarity and limit-break stage from the owned character's server-side template (`OriginalRare`, `GradeCoreId`), rather than the redundant saved `Grade`. SSR template stages 1–3 have cap 160; stage 4 and above have cap 200. Reject an unknown template with HTTP 400 rather than spending resources without knowing its cap.

Validate the original requested target, before any upgrade-table clamping. Ordinary leveling validates its owned character before computing costs. Batch leveling validates every owned character in the request before entering the existing mutation loop, so an invalid later entry, including a repeated character, cannot follow an already-paid upgrade.

Duplicating the cap checks in both handlers risks divergence. Clamping an excessive request can still charge resources and change levels, which does not satisfy the requested rejection behavior.

## Constraints

- Reuse the registered session worktree and its copied static data and synthetic saves.
- Preserve legitimate SSR boundary upgrades and existing R/SR behavior; do not change Synchro Device enhancement levels.
- A rejected request must leave materials, in-memory user state, JSON save bytes and SQL mission triggers unchanged.
- Do not normalize pre-existing over-cap saves as part of this fix.
- Integrate, synchronize, run the full regression entry, and back up the exact integration commit to the authorized Fork branch. No production installation is requested.

## Verification

Extend `CharacterLevelCapAudit` with all SSR break-stage boundaries, original target versus clamping, stale saved grades, invalid batch entries in either order, duplicate entries, valid multi-character batches and unknown templates. First observe the tests fail against unchanged handlers, then observe them pass after the shared validation is added. Run the complete project regression suite after integration and synchronization.
