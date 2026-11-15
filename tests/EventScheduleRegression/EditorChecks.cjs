const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(process.argv[2], 'utf8');
class Element {
  constructor() { this.value = ''; this.dataset = {}; this.listeners = {}; this.disabled = false; this.classList = { toggle() {} }; }
  addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); }
  async fire(type, event = {}) { if (type === 'click' && this.disabled) return; for (const fn of this.listeners[type] || []) await fn(event); }
}
function harness() {
  const ids = Object.fromEntries(['eventScheduleDialog','eventScheduleForm','scheduleOperation','scheduleDays','scheduleEndBeijing','scheduleFeedback','scheduleSubmit','scheduleClose','scheduleCurrentEnd','scheduleBannerId','scheduleEventName','scheduleDaysFields','scheduleDateFields','schedulePresets','scheduleActionHint','scheduleResetClock'].map(id => [id, new Element()]));
  const dialog = ids.eventScheduleDialog;
  dialog.showModal = () => { dialog.open = true; };
  dialog.close = () => { dialog.open = false; };
  ids.eventScheduleForm.action = 'http://test/admin/Events/Schedule';
  ids.eventScheduleForm.reportValidity = () => true;
  const cells = [83500, 40123].map(id => {
    const cell = new Element(); cell.dataset = { eventId: String(id), endUtc: '2030-01-01T00:00:00Z' };
    const children = { '.schedule-end': new Element(), '.schedule-remaining': new Element() };
    cell.querySelector = selector => children[selector];
    return cell;
  });
  const buttons = cells.map((cell, index) => {
    const button = new Element(); button.dataset.bannerId = String([10124,10123][index]);
    button.closest = selector => selector === '.schedule-cell' ? cell : { querySelector: () => ({textContent: ['A','B'][index]}) };
    return button;
  });
  let resolveFetch, rejectFetch;
  const context = { document: { getElementById: id => ids[id], querySelectorAll: selector => selector === '.schedule-cell' ? cells : selector === '.schedule-edit' ? buttons : [] },
    window: { addEventListener() {}, setInterval() {} }, i18n: { t: key => key }, URLSearchParams,
    FormData: class { [Symbol.iterator]() { return [['BannerId', ids.scheduleBannerId.value]][Symbol.iterator](); } },
    fetch: () => new Promise((resolve, reject) => { resolveFetch = resolve; rejectFetch = reject; }) };
  vm.runInNewContext(source, context);
  return { ids, dialog, buttons, resolve: payload => resolveFetch({ok: true, json: async () => payload}), reject: () => rejectFetch(new Error('network')) };
}
(async () => {
  const h = harness();
  await h.buttons[0].fire('click');
  const pending = h.ids.eventScheduleForm.fire('submit', { preventDefault() {} });
  assert.equal(h.ids.scheduleSubmit.disabled, true);
  assert.equal(h.ids.scheduleClose.disabled, true, 'Cancel must stay disabled while a save is pending');
  let cancelled = false;
  await h.dialog.fire('cancel', { preventDefault() { cancelled = true; } });
  assert.equal(cancelled, true, 'Escape must not close an editor with a pending save');
  await h.ids.scheduleClose.fire('click');
  assert.equal(h.dialog.open, true);
  await h.buttons[1].fire('click');
  assert.equal(h.ids.scheduleBannerId.value, '10124', 'a second event must not replace the pending editor');
  h.resolve({ eventId: 83500, endUtc: '2030-02-03T04:05:06Z', serverUtc: new Date().toISOString() });
  await pending;
  assert.equal(h.ids.scheduleEndBeijing.value, '2030-02-03T12:05:06');
  assert.equal(h.ids.scheduleClose.disabled, false);
  await h.ids.scheduleClose.fire('click');
  await h.buttons[1].fire('click');
  assert.equal(h.ids.scheduleBannerId.value, '10123');
  const rejected = h.ids.eventScheduleForm.fire('submit', { preventDefault() {} });
  h.reject();
  await rejected;
  assert.equal(h.ids.scheduleClose.disabled, false, 'network failure must release the editor');
  assert.equal(h.ids.scheduleSubmit.disabled, false);
  console.log('PASS: pending saves cannot close or replace an editor; success/failure releases controls');
})().catch(error => { console.error(error); process.exitCode = 1; });
