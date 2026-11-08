# Complete tower and museum rankings

Goal: repair the empty tower and museum cards reported in the two client screenshots, including their detail, milestone and reward flows. Execute in this chat's registered worktree and retain the existing campaign/corporation behavior.

Evidence: `RankingService` currently limits validation, score snapshots, overview, settlement and server rewards to types 0..4. The shipped enum adds towers 5..9 and museum 10..11. The overview has separate museum fields, and museum detail/achievement/reward requests have dedicated protobuf messages. The old regression explicitly expects only five boards.

Constraints: no live tests or save edits; use the collaboration test/integrate/sync/publish/seal/deploy entry points. Keep museum historical challenge NPC archives separate from players' earned ranking points. Installation requires the current chat's authorization after a concrete release is prepared.

## Tasks

- [x] Add failing authenticated protobuf tests for five tower categories, museum total and per-hall cards/details, milestones, rewards, invalid types/groups and refresh/restart idempotency. Inspect the shipped target and mission tables and client request paths.
- [x] Extend `RankingService` snapshots to tower progress and earned museum mission points; populate all three overview collections and implement the dedicated museum routes in `Ranking/MuseumHandlers.cs`. Use one score source for cards, details and milestones.
- [x] Extend first-observed progression stamps, milestone settlement and server reward selection to the added categories. Preserve reserved winners, save rollback, bans, stable ties and existing mail ownership behavior.
- [ ] Run the new regression RED then GREEN, review the complete diff independently, commit and integrate/sync, run all isolated regressions and publish the current integration. Prepare and seal a guarded installer with backups, rollback and health checks if deployment is needed.

Review focus: client route/field fidelity; hall IDs and total/group score isolation; zero scores and absent participants must remain honest; duplicate or invalid mission receipts must not inflate points; existing ranking claims must not reissue rewards.

## Evidence ledger

- Baseline: `983157329a5e6d5e1e866e456b527e1f98c011b5`; old RankingRegression 21, TowerRegression 5, SoloRaidMuseumRegression 7 groups pass in `.nikke/checks/20261108-200204-150741`.
- Ruling: this is completion of the reported ranking defect with a fixed protocol/data contract, so implementation continues under the user's repair/worktree instruction; no additional design approval is needed.
- RED: `.nikke/checks/20261108-201202-444675` reproduces 11 failures against the original implementation (20 other checks pass).
- GREEN: `.nikke/checks/20261108-201523-928293` passes Ranking 31, Tower 5 and SoloRaidMuseum 7 groups. `.nikke/checks/20261108-201854-421189` passes Ranking 32 groups, including fresh-process persistence of added scores/milestones/reward receipts.
- Independent whole-change review found no P0-P2 issues; its P3 total-overview/detail probe omission was corrected before integration.
- Client evidence: live historical log `app-20261108.log:75378-75395` proves `/ranking/get/museumtotal/rankinglist` previously fell back to the empty handler. Other museum routes follow the same family and the existing ranking suffixes; actual client hits are still unverified. Tower scores use cleared `TowerRecord.Id`, consistent with stage-based score encoding; the client display decoder is not yet verified.
- Read-only live save observation: human Tribe Tower 95, all 100 managed NPCs have tower progress; the human has no claimed museum mission receipts. An empty museum points board remains valid until an account earns points. Fixed historical museum challenge NPCs remain separate.
