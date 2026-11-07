# NPC teams, five collection tiers, and 100 managed players

The user requested owned-character SSR teams with sustainable burst chains and skill synergy, five tiers for SSR inventory and skills, and expansion from 30 to 100 NPCs. They chose to retain all existing characters and derive existing NPC tiers from owned SSR counts.

## Approach and defaults

Use an owned-roster selector with hard burst feasibility checks before team scoring. Fixed team templates would fail for the current random inventories; unrestricted power sorting would repeat broken burst chains. Evaluate five-character SSR combinations using the installed Character, CharacterSkill, and CharacterShot tables, and explicit, documented skill interactions. Prefer the shortest sustainable cadence, then support/damage and skill synergy. Stable seed-based tie breaking keeps NPCs varied and restarts repeatable.

Check actual slot order, cooldowns, re-entry, and all-stage burst characters over repeated cooldown states. Validate burst eligibility and cooldown only; charge generation, enemy behavior and actual combat acceptance remain outside this server simulation. Do not assume conditional cooldown reduction when proving a team sustainable. This conservative choice can prefer an extra backup over a conditional solo burst unit.

Score and label each supported interaction with its own required burst caster. Determine buff availability separately from that conservative proof, accounting for guaranteed Blanc/777 cooldown reduction from installed skill data so a shadowed backup's burst is not credited. Skill descriptions used for the rules come from the installed Locale_Skill table and SkillInfo records; unrecognized mechanics remain neutral.

Preserve all existing owned character IDs, serial numbers, levels, grades, story progress, guild membership, activity schedules and non-NPC users. Existing inventory limitations may require 40-second cadence: Qingning has no 20-second SSR stage I; Wusheng owns only one SSR stage III. Never invent a new card to hide that limitation.

## Five collection tiers

Collection tier is separate from the existing three activity tiers, preserving daily progression and guild contribution limits.

| Tier | Existing owned SSR count | New inventory count | Main skill range | Reserve skill range |
|---|---|---|---|---|
| 1 | 5–12 | 8–12 | 2–3 | 1–2 |
| 2 | 13–18 | 13–18 | 4–5 | 2–3 |
| 3 | 19–24 | 19–24 | 6–7 | 3–4 |
| 4 | 25–30 | 25–30 | 8–9 | 4–5 |
| 5 | 31+ | 31–40 | 9–10 | 5–6 |

Values are implementation defaults chosen by the assistant. Existing higher skills are retained. New recruits are SSR, trigger tier recalculation and team selection, and ongoing skill training operates on actual team serial numbers within tier limits. New inventories contain a sustainable 20-second SSR core before their remaining random SSRs are selected; all five tiers occur across the added 70 NPCs.

## Persistence and presentation

Version each NPC's team migration so restart is idempotent. Save changes under the existing JSON snapshot lock and rollback on save failure. Synchronize profile and campaign squads, and NPC rookie arena squads; rebuild special arena squads without repeating characters across rounds. Real-player arena teams are untouched.

Expose collection tier, owned SSR count, main skill range, nominal burst cadence and chosen synergy in the admin NPC page. Explain the distinction between collection tier and activity tier. Keep existing population limit 200; the user's requested deployment target is exactly 100. A target-count startup operation creates only the missing population with the existing durable creation journal, so retries cannot create another 70.

## Validation and deployment

Add tests for sole re-entry stage I/II, all-stage use, 40/60-second cooldowns, five distinct owned SSRs, synergy preference, tier boundaries and skill training, migration preservation/idempotence/rollback, and target-count expansion/recovery. Validate all current 30 inventories and a new 100-player fixture. Run repository regression suites and publish the current stacked source. Stop only the game server after checking that the game is closed, back up executable and both databases, install, migrate and expand, then verify real stored teams and authenticated profiles. Report actual server verification separately from in-game acceptance.
