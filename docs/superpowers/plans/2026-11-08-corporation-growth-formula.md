# Repair corporation growth scoring

User scope: establish the complete official growth formula, implement it in this registered worktree, verify it, then integrate/publish/seal/deploy and restart through the collaboration workflow. Installation is authorized in this chat; check game exit again inside deployment.

Baseline: `8c29728b75df13a162bd3a3775edaa312ebf334a` (contains the earlier tower/museum ranking completion and subsequent integrated fixes).

## Formula evidence and decisions

- The client `Locale_System` help `HELP_POINT_RANKING_03_02..05` enumerates rarity, manufacturer/class Recycling Room research, character levels, and skills. `05_01` explicitly limits research to manufacturer/class. Source snapshot SHA256 `9e689e80272294997a3d7421c9454b849e09a2abc54591d953cb0ce6f7581188`; extracted evidence is `.nikke/ranking-client-locale-evidence.json`.
- `RankingPointTable` supplies rarity/breakthrough/core points: SSR 300 per acquired copy through core 7, SR 50 per acquired copy, R rarity contribution zero. `SkillLevelPointTable` supplies cumulative points per skill; levels 1..10 give `0,5,10,15,30,45,60,90,120,150`.
- Research adds one point per relevant research level per owned character: manufacturer research + that character's class research. Common/personal research does not count. A direct official-server experiment shows 16 characters times three research levels = 48 points.
- Character level contribution is native saved level plus registered Synchro level. A level-1 character added to Synchro 350 increases score by 350, not 349. A Korean calculation correction gives native 100 + Synchro 300 = 400. The latter lacks complete before/after screenshots; retain this evidence limitation. Do not add an arbitrary global constant to reconcile another creator's unexplained 199-point discrepancy.
- Bond, equipment, cube and collection do not contribute. The complete explicit client help and direct official-server exclusion experiment support this. Preserve the user's bond data and verify it does not alter growth score.
- Use one whole character record with the greatest complete growth contribution per NameCode; do not combine the strongest rarity, skill and level from different duplicate records. Unknown character definitions do not participate; malformed empty/zero-CSN Synchro slots must not contribute. Clamp negative input components and saturate the final protobuf int score safely.

Public primary observations: https://www.youtube.com/watch?v=3YVsuhp3aq8 (2024-11-22, 1:20 exclusions; 1:40 research; 1:50 level/skills), https://arca.live/b/nikketgv/91375094 (2023-11-13 correction), and https://forum.gamer.com.tw/C.php?bsn=36390&snA=9980. Captures and raw research notes are in the session workspace `.nikke/official-formula-research`.

## Execution

- [x] RED: add authenticated endpoint regressions for all four terms, additive Synchro, SR/R, research category/ownership boundaries, bond exclusion, duplicate records, and first-observed achievements after real skill/research/Synchro actions. Run through `nikke_workflow.py test`.
- [x] Implement a pure corporation-scoring helper and load the skill-point table. Extend the existing post-action progression stamp for native levels, three skills, Synchro state/assignments and research levels. Keep observation outside action/NPC locks.
- [ ] GREEN: update old tests which intentionally excluded level, run targeted tests, independent review and full isolated regression. Preserve existing milestone ranks/timestamps, reward claims, mail receipts and save rollback.
- [ ] Integrate/sync, run the full suite on current integration, publish, prepare a new guarded installer, seal/check/deploy. Discard no existing live progression. Verify hashes/health, preservation and authenticated score responses after restart; keep client acceptance separate from server evidence.

## Ledger

- Existing implementation only sums rarity/breakthrough/core and explicitly tests that levels are ignored. This is the reproduced design defect; the point table alone never represented the complete official formula.
- No live state has been modified during formula research. All test fixtures are synthetic and live audit reads are separate.
- RED `.nikke/checks/20261108-232015-283281`: one passed, twelve failed against the old formula. Initial GREEN `.nikke/checks/20261108-232333-957193`: all 13 growth and 32 existing ranking groups passed.
- Independent review found an unknown-TID record could indirectly raise unenhanced Synchro. Added a failing authenticated regression (`.nikke/checks/20261108-232910-103877`: 12 passed/1 failed) and filter the native-level candidates inside the ranking helper, leaving other game systems unchanged.
- A read-only calculation on the persisted live snapshot (`.nikke/company-growth-predicted-before-deploy.json`) produced new human corporation totals 30010/24154/26728/29772 for Elysion/Missilis/Tetra/Pilgrim. These are predictions until deployment probes confirm them; Tetra NPC 1500000059 calculates 28460. No NPC or player stats were adjusted to change rank order.
