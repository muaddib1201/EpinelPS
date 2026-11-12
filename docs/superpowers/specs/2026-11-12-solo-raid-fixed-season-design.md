# Fixed solo raid season

The user requested confirmation that the local boss matches the current official boss, and a fixed season deadline that actually ends play instead of advancing every day.

The screenshot displays Goddess of Victory. Current public raid notices identify season 43 as Goddess of Victory (Wind, weak to Fire), from 2026-11-12 03:00 UTC through 2026-11-18 19:59:59 UTC. Treat 2026-11-18 20:00 UTC as the exclusive end. References: https://nikkehub.com/en/raids/solo and https://nikke.gg/october-29-patch-notes/ . The official update notice linked by the calendar is https://nikke-kr.com/newsdetail.html?content_id=5b8bea6da2a9aa420ca8dc2a04c7968918b9 (its body requires JavaScript).

## Approach

Use one explicit `SoloRaidSeason` configuration with a raid manager ID and fixed UTC timestamps. Existing runtime configurations that omit the new property receive the embedded season 43 defaults (manager 1000044), so deploying the executable requires no live configuration rewrite. This is a focused bug fix following the user's requested fixed-time behavior. A rolling calendar and a deadline anchored to a server restart both move the deadline incorrectly. A general automatic rotation/reward scheduler is outside this change.

Visible/start: 2026-11-12 03:00 UTC; exclusive end: 2026-11-18 20:00 UTC. For local post-season display, settlement begins at the end and the results remain visible until 2026-11-25 20:00 UTC. These display/settlement choices are local policy, not verified official reward distribution times. No new reward distribution is implemented.

Both the reported raid ID and its period must come from this configuration. Adding a higher static-data ID must not activate an unscheduled boss. Before start and at/after end, reject enter/open/damage/close/fast-battle requests with the protocol's `Failure` result and no score, reward, or attempt mutation. Keep standings and recorded teams readable. Daily attempts retain their existing 04:00 China time reset.

Validate positive raid ID and ordered dates. Preserve saves and all previously integrated fixes. Tests use only this session's copied static data and synthetic accounts. Integrate, sync, run regression suites, publish/seal, and back up the integrated source to the authorized Fork. Deploy only through the unified locked entry, after verifying the game is closed, with program/save backup, rollback and HTTP plus authenticated read-only checks.
