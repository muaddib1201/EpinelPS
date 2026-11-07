# Support character usage repair

**Goal:** Persist and report temporary character usage, consuming one use per participating character after a successful battle.

**Architecture:** Load support content limits and character groups from the existing static data. Keep counters in daily reset data, and capture the selected support characters at battle entry so failures and repeated settlements do not consume extra uses. Reuse the configured daily reset schedule.

**Scope:** Campaign, tower variants, story events and archives that currently accept support characters. Preserve the existing customized working tree and deployed user data.

- [x] Add a real-handler regression that fails against the empty usage response.
- [x] Load support tables; implement per-content/per-character counts, entry validation and successful settlement with a pending battle snapshot.
- [x] Connect query, campaign, quick campaign and tower handlers; keep administrative completion and owned characters unaffected. Events and archives have UseValue=0 and need no consumption hook.
- [x] Verify failures, retries, exhaustion, separate contents/teams, serialization, old saves and daily reset using real static data and request handlers.
- [x] Build and publish, back up the deployed executable, replace it, restart the local server if running, and verify startup.

**Validation:** Run the regression console project and a Release publish using .NET 10. Do not use the live user database for tests. Do not reconstruct historical usage from incomplete logs.

**Review decisions:** Honor the explicit quick-battle request squad, falling back to the saved squad only when omitted. Defer the campaign completion save until after support settlement so both changes are written together. Preserve the pre-existing working-tree customizations and update only the deployed executable.

**Result:** All 20 regression scenarios passed. Release publish succeeded. The deployed executable SHA256 is `48B7B5AB091579F79BE76E8FE62290BAE11C9665D32E75ACDCCF6893A3FCACE7`. The previous executable and current save/database files were backed up to `D:\Claude Lab\NIKKE\backup\server-20261102-014114-support-counts`. The replacement started on 2026-11-02 01:41 (UTC+8), and the local HTTPS admin page returned HTTP 200. Existing daily reset remains 20:00 UTC / 04:00 UTC+8. Historical unrecorded consumption was not reconstructed.
