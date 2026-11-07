# Guild activity and shop mission repair

> Execute in this session. Preserve the current stacked local fixes; source snapshots are in `.tools/activity-mission-20261107/source-before`.

**Goal:** Credit claimed daily mission points to the player's Union and publish successful normal shop purchases to mission sync.

**Architecture:** Store member and Union daily contribution histories in JSON. Use the existing reward mutation scope and a durable trigger outbox to commit rewards, claims, activity and mission notifications together. Both humans and managed NPCs contribute through the same activity calculation.

**Tech stack:** C# / .NET 10, JSON persistence, SQLite trigger history, protobuf handlers.

**Requirements:** User's two screenshots and request; daily contribution limit 100, rolling seven game days; retain configured UTC reset hour and existing Union grade thresholds. Do not interrupt the running game.

## Tasks

- [x] Add isolated endpoint regression cases for successful/rejected single and batch purchases, daily reward claims, duplicate claims, contribution limits, rollover and upgrade.
- [x] Run the tests against the existing server; confirm missing shop trigger and missing human Union contribution.
- [x] Add a shared durable mission trigger delivery helper; commit shop and daily mission changes before publishing trigger events.
- [x] Add dated Union/member contributions, shared NPC accounting and daily/recent projection in the guild directory. Restore backed-up state on persistence failure.
- [x] Recover demonstrable legacy claimed points once. Backfill only shop purchases whose retained shop interval is wholly within the current game day; do not invent old purchase dates or regrant items.
- [x] Verify isolated tests, failure recovery and fresh-process persistence, then relevant existing suites.
- [x] Publish to a staging directory, check live executable hash, wait for the game to exit, back up live executable and JSON/SQLite files, then install with rollback and authenticated read-only verification.

## Review focus

Failed purchases must contribute zero; duplicate daily claims must not pay twice; a failed JSON save must preserve claim and activity state; a temporary SQL failure must leave a deliverable outbox; NPC updates and the configured day boundary must retain human seven-day contribution history.

## Result

25 isolated regression groups and 2 fresh-process groups passed. Full regression run: 28 executions / 262 groups / 0 failures. All four independently reviewed issues were reproduced and fixed (retained account references after rollback, claim reset at rollover, NPC transfer display, account-wide budget across leave/rejoin).

Installed 2026-11-07 02:58 Asia/Shanghai after game exit. Published executable and real-copy migration audits passed. Live authenticated guild projection: player daily 100, recent 100; Union activity 100 and experience 100/9000. One durable ordinary-shop event restored 25 purchase units. Wallets, inventory, reward claims, NPC teams/profiles, SQL identities and previous triggers passed preservation checks. Backup: `D:/Claude Lab/NIKKE/backup/server-20261107-025807-132-before-activity-mission`. Client visual acceptance remains pending; the existing 100-point daily completion overlay is expected.
