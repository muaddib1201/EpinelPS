# NPC breakthrough repair implementation plan

> **For agentic workers:** Use superpowers:executing-plans to execute inline. The user requested repair of the confirmed NPC grade/template mismatch.

**Goal:** Keep NPC breakthrough grades, character templates and growth consistent without changing character identities, levels, skills, teams, profiles, schedules or human saves.

**Architecture:** A shared NPC-only normalizer resolves the variant for the same NameCode and the greater of saved/template breakthrough. Preserve the existing SSR rule: above level 160 requires at least grade 3. Apply it during creation, recruitment, growth and idempotent startup repair, using the existing snapshot/save rollback mechanism.

**Tech stack:** C#/.NET 10, current CharacterTable/GradeCoreTable, existing ManagedNpcRegression harness, Python JSON/SQL preservation audit.

## Constraints

- Keep the current deployed counsel-unlock and Traditional Chinese profile changes.
- Respect the current data: SSR zero through two stars have level cap 160; three stars have cap 200.
- Existing higher breakthrough/core variants never downgrade. Human accounts are excluded.
- Change only NPC CharacterModel.Tid and Grade during existing-save repair.
- Back up and install only after game exit; verify all 100 live profiles after installation.

## Tasks

- [x] Add regressions for grade-3 creation, base-grade boundaries, same-character variant repair, higher-core preservation, human exclusion, save failure rollback, idempotent/restart repair, growth and recruitment.
- [x] Run the new creation regression against current production code and confirm its grade-3 template failure.
- [x] Add ManagedNpcBreakthroughs and call it from factory, growth, startup and worker. Reuse ManagedNpcService.Change for save rollback.
- [x] Run focused and existing NPC regression/restart checks, plus arena/guild/profile consumers.
- [x] Publish the current source, prepare a copy of current saves, and audit that only NPC template/grade fields changed and SQL tables are preserved.
- [x] With existing authorization, back up the live server after game exit, install, repair in preparation mode, audit and start the server.
- [x] Read all 100 live profiles and verify every template agrees with its saved breakthrough, and existing profile/guild data remains intact.
