# MARIA-n activity countdown

The user approved fixing the reported countdown, testing it, deploying it through the project workflow, and pushing the verified integration to their Fork.

MARIA-n (event 83500, lobby banner 10124) and its story, mission, shop, challenge and minigame children must end at **2026-11-18 19:59:59 UTC**, equivalent to November 19 04:59:59 UTC+9 / 03:59:59 Beijing time. Both event-list responses must send that fixed deadline for EventEndDate and EventDisableDate. Refreshing the lists must never extend it, including after expiry. Keep existing start dates, claimed missions, rewards and daily reset hour unchanged.

Sources: https://nikkehub.com/zh-tw/anniversary and the official October 29 update https://game.naver.com/lounge/nikke/board/detail/8369216 (also https://prtimes.jp/main/html/rd/p/000000147.000145037.html).

## Approach

Use one deadline resolver in EventHelper for the known parent and records linked by ParentsEventId or SetField. Unknown/private events retain their current fallback. The current schema has no banner dates, so uncommenting the old date assignments cannot compile; assigning this date globally would incorrectly expire unrelated events. No online scraping or save migration is needed for this fixed schedule repair.

## Verification and release

Exercise real protobuf handlers with session-local static data and synthetic saves: parent/child deadlines, both responses, repeat requests, screenshot remaining time, claim preservation, unrelated events and reset settings. Run the full project regression entry point after integration and sync. Publish, review and seal a session-owned installer. The installer checks game exit, backs up program and saves, verifies HTTP/HTTPS and authenticated read-only event responses, checks human save preservation, and rolls back on failure. Push the exact verified integration commit and verify ls-remote.
