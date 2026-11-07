# NPC synchro and duplicate recruitment plan

**Goal:** Complete NPC growth using five three-star level-200 SSRs to unlock enhanced synchro, and duplicate recruitment for limit breaks/core enhancement.

**Rules:** Base levels use current GradeCoreTable caps (SSR0–2:160, SSR3/core:200; SR:160; R:80). Beyond200, grow only SynchroDeviceLevel after five distinct three-star SSRs reach200. Preserve existing effective main-team levels, current stars/cores, ownership, teams, progress, identities, Traditional Chinese profiles, schedules and human saves during migration. Repeated SSRs advance one grade, becoming core+1 through+7 after grade3; full cores are not advanced. Keep5–9 day recruitment schedule, alternating new collection and duplicates, first cycle a new collection. Target mains first for duplicates; unavailable branches fall back to the other branch.

**Implementation:** ManagedNpcGrowth handles versioned migration, synchro slots/cap, real growth, and recruitment. Existing transaction snapshots retain null/paused management settings. Reuse CharacterTable.GrowGrade for real duplicate upgrades. Profile and NPC shooting data/admin page use effective levels. Existing complete resource-economy simulation remains outside this change; NPC slot-item allowance models capacity for their simulated roster.

- [x] Add failing regressions for 200-level migration, prerequisite enforcement,160 wall, base caps,0-star synchro, core+1/+7, duplicate ownership preservation and null-settings/save rollback.
- [x] Add shared growth service and integrate creation, tick, recruitment, startup/worker, profile and admin level display.
- [x] Validate effective levels and identity preservation against a current save copy, including all16 enhanced-synchro candidates.
- [x] Run NPC and existing regression suites, request one independent code review, and resolve findings.
- [x] Publish, audit staged current saves, back up/install after game exit, and verify all100 live profiles, synchro states, names/bios/guild membership.
