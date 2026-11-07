# Counsel unlock implementation plan

> Execute inline with superpowers:executing-plans; perform one fresh review before deployment.

**Goal:** An authenticated administrator unlocks one selected owned character's counseling entry, and the player's missing Marian Queen 04 is restored.

**Architecture:** Load AttractiveCounselTable. A dedicated admin controller lists owned-character conversations and atomically appends the validated table ID. The existing user-edit page hosts a separate AJAX form. The existing game journal reads the stored IDs unchanged.

**Tech stack:** .NET 10, ASP.NET MVC/Razor, protobuf, existing atomic JSON store and i18n.

**Spec:** ../specs/2026-11-07-counsel-unlock-design.md

## Global constraints

- No client patch, privileged client read, Windows UI control, or random-selection change.
- Only the chosen character's collection list may change; no counseling counts, bond gains, currency, scenario rewards or other accounts.
- Admin authentication plus CSRF; reject unknown accounts, unowned characters, mismatched and invalid IDs.
- Idempotent concurrent updates; roll back JSON write failures.
- Preserve all current local changes and the latest NPC profile release. No unrelated staging/commits.
- Back up live state and wait for game exit before replacement.

## Tasks

- [x] Add isolated HTTP/journal regression tests, observe missing endpoints/UI fail.
- [x] Implement admin GET/POST, static table, translated form and atomic unlock; validate all cases.
- [x] Run relevant existing regressions, fresh focused review, publish from current source and back up/deploy.
- [x] Use the authenticated new endpoint to unlock only the live player's missing 3384; verify account diff, game journal response, and persisted 20/20. Report manual game display acceptance separately.

## Review focus

Cross-character IDs; users without a bond; duplicate/concurrent submissions; stale selected data after switching characters; write failure leaving partial state.

## Verification and rulings

- RED: 0/10, missing form and endpoints. GREEN: 10/10 after implementing the controller, table and form. The shared-connection SQLite fixture initially caused concurrent auth reads to fail; changed it to the same per-scope connection-string pattern used by Program.cs. Production authentication was unchanged.
- Final related runs: CounselUnlock 10, CounselReset 7, DailyMission 15 + 3 separate-process restart, BondEpisode 6, ActivityMission 25; total 66 passing groups. JavaScript syntax and all four translation JSON files validated. Full build has 23 existing warnings, no errors.
- Fresh read-only review found no blocking server defect. Fixed its minor UI finding: language changes now update character names while preserving nameCode. Browser-script runtime and game-screen verification remain manual; no Windows UI control was used.
- Deploy only the current source release and four translation files. Guard against the latest NPC-profile program hash FAC05F69DF05B2C056688FB3EE824F21E788FE3CE11329B9200C88A130751800 changing first. The one-time real-account unlock is restricted to 1552239268 / 5185 / 3384 and verifies an exact human-account diff.
- Deployed SHA256 E2130325A7117B5AA1F48F898856A2374DFC9E7309EAC3E8DC74EB2378F763C6. Coherent backup: `D:\Claude Lab\NIKKE\backup\server-20261107-135235-111-before-counsel-unlock`. Authenticated pre-unlock read returned 19/20 and preserved all human data.
- The authenticated POST added only 3384 to the requested real player's Queen bond. Count and game journal became 20/20. A subsequent real service restart retained the unlock; the read-only protocol/admin probes and exact human-account audit passed, and all four installed translation hashes matched. See live-unlock.json and final-health.json in this task's .tools folder. No native client inspection or Windows UI actions were used for this implementation.
