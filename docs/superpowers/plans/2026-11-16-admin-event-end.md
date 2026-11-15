# Admin event end times implementation plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task by task, then request one whole-branch review.

**Goal:** Let administrators set and extend a fixed deadline shared by an event and its related children, and test before deployment.

**Architecture:** Add a main-event-keyed UTC deadline dictionary to CoreInfo and a schedule service for resolution/validation. Route the existing event responses through it. Keep the existing selection view and add one dialog using an authenticated antiforgery-protected POST.

**Tech stack:** C#/.NET 10, ASP.NET Core MVC/Razor, existing Bootstrap/admin i18n, existing JSON configuration persistence and regression runner.

**Spec:** docs/superpowers/specs/2026-11-16-admin-event-end-design.md

## Global constraints

- Use this chat's registered source/workspace directories for all edits, fixtures, builds and checks.
- Beijing display/input is UTC+8; persistence and protocol timestamps use UTC.
- Positive custom days are restricted to 1–3650. Presets are 14, 20 and 21.
- Preserve start times, progress and claims. No live changes or deployment.
- Follow integrate → sync → full regression → authorized GitHub backup after implementation/review.

## Review focus

- A gacha/login/payback child without a direct parent link must still inherit its actual main event's deadline without affecting another event.
- Browser/server timezones must not reinterpret Beijing input or shift the displayed value.
- Saving activation or extending an expired event must not reset an existing countdown or player claims.
- Invalid and concurrent changes must not leave partial configuration or lose an extension.
- The modal must not submit the outer activation form or toggle the activity checkbox; pagination/search/language changes must keep its controls usable.

## Task 1: Fixed deadlines and real regression harness

Files: tests/EventScheduleRegression/Program.cs, tests/EventScheduleRegression/AdminScheduleChecks.cs; EpinelPS/Models/CoreInfoModel.cs; EpinelPS/Services/EventScheduleService.cs; EpinelPS/LobbyServer/Event/EventHelper.cs; EpinelPS/LobbyServer/Event/EventLoginGet.cs.

- [ ] Add failing protocol tests using a deserialized root deadline: explicit overrides apply to list/joined/login, nested parents and set-field links; unrelated groups retain their own dates; the same deadline survives requests before and after expiry.
- [ ] Run the EventScheduleRegression suite through nikke_workflow.py and confirm failures are missing override behavior.
- [ ] Add EventEndUtc configuration and centralized root/deadline resolution. Preserve official MARIA-n and legacy fallbacks when no override exists; route all three event response types through the resolver.
- [ ] Rerun focused regressions and confirm the protocol tests pass.

## Task 2: Admin editor and validation

Files: EpinelPS/Controllers/AdminPanel/AdminController.cs; EpinelPS/Controllers/AdminPanel/AdminController.EventSchedules.cs; EpinelPS/Models/Admin/EventScheduleInput.cs; EpinelPS/Views/Admin/Events.cshtml; EpinelPS/wwwroot/admin/assets/js/event-schedules.js; the four existing admin i18n JSON files; tests/EventScheduleRegression/AdminScheduleChecks.cs.

- [ ] Add real loopback MVC tests for set-days, explicit Beijing date, extensions, expired renewals, activation initialization, reload, invalid values/actions/IDs, unauthorized/CSRF requests and unchanged saved claims. Confirm missing-route failures before implementation.
- [ ] Implement authenticated/antiforgery-protected schedule edits under JsonDb.SaveLock, returning the persisted UTC deadline and canonical main event ID. Initialize only missing ordinary deadlines when activation is saved.
- [ ] Add a localized end/countdown column and one modal outside the activation form. Offer 14/20/21/custom days, explicit Beijing date, optional 03:59:59 and extensions. Update all rows for the same main event on success; keep selection controls independent.
- [ ] Verify the focused suite and exercise the rendered page/controls on the isolated loopback host. Record evidence inside .nikke/admin-event-end.

## Task 3: Review, integrate and verify

- [ ] Review the complete diff with a fresh reviewer; fix material findings with regression coverage.
- [ ] Commit only relevant source/docs/tests; integrate using the project entry point and sync to the latest integration ref.
- [ ] Run test --suite all and inspect the evidence. Report any unrelated failures accurately.
- [ ] Inspect the exact new history for secrets/player saves, push the verified integration ref to the authorized GitHub backup, and verify it with ls-remote.
- [ ] Report implemented controls and test results, clearly stating that deployment and game-client acceptance are pending.
