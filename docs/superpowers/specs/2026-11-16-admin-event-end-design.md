# Admin event end times

The user requested implementation and testing of an event deadline editor in the existing admin panel, including 14/20-day countdowns, extensions, and a shared deadline for the main event and related children. Deployment is not authorized in this turn.

## Approach

Store fixed UTC deadlines in the existing server configuration, keyed by the main event ID. Resolve child events through their parent/set-field links and the existing associated recruitment/login/payback relationships. This keeps one source of truth for every banner and its children. A new SQL table would add a migration and two persistence paths for this small configuration feature; independently storing each child deadline would allow them to drift.

## Behavior

- Preserve the existing activity selection page and add an end-time/countdown column and an editor dialog.
- Display and edit dates in Beijing time (UTC+8), independent of browser/server locale. Store UTC values.
- Offer exact countdowns from the time of saving: 14, 20, 21 days and custom positive days (1–3650).
- Also accept a specific future Beijing date/time. Include a shortcut to 03:59:59, matching the commonly used official 04:59:59 UTC+9 cutoff. This is an optional clock shortcut, not a claim that every official event shares a schedule.
- Extend from the later of the existing deadline and now. An expired event can therefore be reopened for the full requested number of days.
- Save a fixed 14-day deadline for selected events that have no explicit or known official deadline when their activation is saved. Do not overwrite an existing setting merely by reselecting an event.
- Keep MARIA-n's existing official deadline as the fallback; an explicit admin deadline overrides it. Unconfigured legacy events keep their old fallback until the administrator saves an activation or a deadline.
- List, joined-event and login-detail responses use the same configured deadline for the main activity and related children. Expired deadlines remain fixed; reading an event never silently renews it.
- Time changes do not activate/deactivate banners, change start times, reset player progress, daily reset or reward claims.
- Require administrator authentication and antiforgery validation on the new write route. Reject invalid IDs/actions/days/dates without partial writes. Serialize edits with the existing configuration save lock.

## Verification

Exercise real MVC HTTP requests with a synthetic administrator, session-local static data and SQLite/database output. Cover the public event responses, fixed deadlines across refresh/clock boundaries, both parent link fields, indirectly associated children, independent event groups, invalid/unauthorized/CSRF requests, persistence/reload, renewal of expired events and preservation of claims. Inspect the rendered admin panel and exercise its controls against that isolated host. Run the full project regression suite after integration and push the verified integration commit to the authorized backup branch. Do not build an installer, deploy, restart the live server, or write live saves.

## References

- Official event example: https://nikke-jp.com/events/valentinemaid/ uses a 04:59:59 UTC+9 cutoff.
- Existing MARIA-n fallback remains 2026-11-18T19:59:59Z.
