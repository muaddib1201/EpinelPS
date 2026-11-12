# Automatic NikkeHub solo raid seasons

The user requested automatic future Boss/season-time synchronization from https://nikkehub.com/zh-tw/raids/solo, without manual configuration. They explicitly require a request to update game data if the next Boss is absent locally. Retain the fixed cutoff and save-preservation behavior delivered in b25892e.

## Source and matching

Read the user's public page at server startup and hourly. Its server-rendered Next.js model contains a `season` object with `id=solo-43`, `kind=solo`, `seasonNo=43`, ISO UTC `startAt=2026-11-12T03:00:00.000Z`, inclusive `endAt=2026-11-18T19:59:59.000Z`, and Boss metadata. Parse the JSON carried by script data without executing JavaScript. Do not scrape localized display dates, execute scripts, or send player data/credentials. Limit response size and network duration, reject redirects away from the fixed source, and validate the season schema/dates.

Match a site's season number to `SoloRaidManagerRecord.MonsterPreset == 10000 + seasonNo`; do not use the maximum manager ID or fuzzy translated Boss names. The observed local manager1000044 has group10043; manager1000043 has group10042. Require one matching manager and complete stage1-8 presets, target monsters and HP data. A schedule cannot provide missing client/server Boss assets. No client, static-pack, or resource-base updater is included.

## Runtime behavior

Enable automatic synchronization by default for existing configs. Allow a single explicit opt-out. Keep fetched schedules separate from gameconfig and player saves. Persist a bounded catalog atomically in `cache/solo-raid-nikkehub.json` under the server application directory; retain the last confirmed catalog through timeouts, errors, broken markup, invalid input, or a failed cache write. Load that cache before accepting game requests so restarts retain the same times offline. Keep the configured fixed season as the fallback.

Activate a complete locally available new season only at its fixed start instant, even when discovered early. Until then retain the current season and its own fixed cutoff. Preserve recent completed schedules and never roll back to an older site's response. New schedules that overlap a different known season are rejected. Normalize the site's last-second end to the exclusive UTC cutoff. Do not shift dates relative to fetch/startup time.

If the next Boss is unavailable, cache its schedule, leave it unplayable and show: **已发现新一期个人突袭，但本地缺少对应 Boss 数据，请先更新游戏数据。** Show this in the existing game system-notice feed, the admin dashboard and server log; automatically clear the hint and activate when the matching local game data is available. External names are plain text/HTML-encoded when rendered.

Use immutable snapshots for schedule publication and capture one season for each battle/info operation. Preserve the existing per-user season-keyed scores and teams. A late old-season damage request without a current-season open run must not create a new-season score; explicit old raid IDs remain rejected. Opening/closing/damage remain subject to fixed inclusive-start/exclusive-end checks under the existing save lock. Runtime synchronization does not claim or distribute rewards.

## Verification and delivery

Offline regression covers real-shaped model parsing, split fragments, escaped names, timezones/inclusive ends, invalid/oversized source, failed HTTP and failed cache writes, offline reload, future activation, stale responses, missing Boss prompt/recovery, score preservation and late submissions. Use synthetic accounts and copied data inside this session's workspace. A separate read-only actual-source probe verifies the current public page.

Review, integrate/sync, run all suites, publish/seal, audit and push the exact integration commit to the authorized Fork, then deploy solely through the unified lock. Installer backs up program/saves plus any existing schedule cache; rollback restores or removes that cache according to its original presence. Verify HTTP/HTTPS, actual successful source/cache sync, fixed period/ranking responses and unchanged SoloRaidData. If the game is running at delivery, request game exit only once the package is ready.
