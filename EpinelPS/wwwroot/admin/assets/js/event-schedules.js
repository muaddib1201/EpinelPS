(() => {
    'use strict';
    const dialog = document.getElementById('eventScheduleDialog');
    const form = document.getElementById('eventScheduleForm');
    const operation = document.getElementById('scheduleOperation');
    const days = document.getElementById('scheduleDays');
    const date = document.getElementById('scheduleEndBeijing');
    const feedback = document.getElementById('scheduleFeedback');
    const submit = document.getElementById('scheduleSubmit');
    const cells = Array.from(document.querySelectorAll('.schedule-cell'));
    let currentCell = null;
    let clockOffset = 0;
    const t = (key, args) => i18n.t('events.schedule.' + key, args);
    const now = () => Date.now() + clockOffset;
    // Explicit UTC+8 arithmetic: browser/server local timezones never interpret this input.
    const beijing = value => new Date(Date.parse(value) + 8 * 3600000).toISOString().slice(0, 19);
    const endLabel = value => value ? beijing(value).replace('T', ' ') : t('unset');

    function refreshCountdowns() {
        cells.forEach(cell => {
            const value = cell.dataset.endUtc;
            cell.querySelector('.schedule-end').textContent = endLabel(value);
            let text = t('unsetHint');
            if (value) {
                let seconds = Math.max(0, Math.floor((Date.parse(value) - now()) / 1000));
                if (seconds === 0) text = t('expired');
                else {
                    const remainingDays = Math.floor(seconds / 86400);
                    seconds %= 86400;
                    const hours = Math.floor(seconds / 3600);
                    const minutes = Math.floor(seconds % 3600 / 60);
                    text = t('remaining', { days: remainingDays, hours, minutes });
                }
            }
            cell.querySelector('.schedule-remaining').textContent = text;
        });
        if (currentCell) document.getElementById('scheduleCurrentEnd').textContent = endLabel(currentCell.dataset.endUtc);
    }

    function updateOperation() {
        const absolute = operation.value === 'absolute';
        document.getElementById('scheduleDaysFields').hidden = absolute;
        document.getElementById('scheduleDateFields').hidden = !absolute;
        days.disabled = absolute;
        date.disabled = !absolute;
        document.getElementById('schedulePresets').classList.toggle('d-none', operation.value !== 'duration');
        document.getElementById('scheduleActionHint').textContent = t(operation.value === 'extend' ? 'extendHint' : 'durationHint');
        feedback.hidden = true;
    }

    document.querySelectorAll('.schedule-edit').forEach(button => button.addEventListener('click', () => {
        currentCell = button.closest('.schedule-cell');
        document.getElementById('scheduleBannerId').value = button.dataset.bannerId;
        document.getElementById('scheduleEventName').textContent = button.closest('.event-row').querySelector('.event-name').textContent;
        operation.value = 'duration';
        days.value = '14';
        date.value = beijing(currentCell.dataset.endUtc || new Date(now() + 14 * 86400000).toISOString());
        updateOperation();
        refreshCountdowns();
        dialog.showModal();
    }));
    document.getElementById('scheduleClose').addEventListener('click', () => dialog.close());
    operation.addEventListener('change', updateOperation);
    document.querySelectorAll('.schedule-preset').forEach(button => button.addEventListener('click', () => { days.value = button.dataset.days; }));
    document.getElementById('scheduleResetClock').addEventListener('click', () => {
        if (!date.value) date.value = beijing(new Date(now() + 14 * 86400000).toISOString());
        date.value = date.value.slice(0, 10) + 'T03:59:59';
    });

    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!form.reportValidity() || submit.disabled) return;
        const body = new URLSearchParams(new FormData(form));
        submit.disabled = true;
        feedback.hidden = true;
        try {
            const response = await fetch(form.action, { method: 'POST', credentials: 'same-origin', body });
            let payload = null;
            try { payload = await response.json(); } catch (_) { /* Expired sessions/antiforgery errors may return HTML. */ }
            if (!response.ok || !payload?.endUtc) {
                const message = response.status === 401 ? t('unauthorized') : (payload?.error ? i18n.t(payload.error) : t('failed'));
                throw new Error(message);
            }
            clockOffset = Date.parse(payload.serverUtc) - Date.now();
            cells.filter(cell => Number(cell.dataset.eventId) === payload.eventId).forEach(cell => { cell.dataset.endUtc = payload.endUtc; });
            refreshCountdowns();
            date.value = beijing(payload.endUtc);
            feedback.className = 'alert alert-success mt-3';
            feedback.textContent = t('saved') + ' ' + endLabel(payload.endUtc);
        } catch (error) {
            feedback.className = 'alert alert-danger mt-3';
            feedback.textContent = error.message || t('failed');
        } finally {
            feedback.hidden = false;
            submit.disabled = false;
        }
    });

    function translateControls() {
        document.querySelectorAll('.schedule-preset').forEach(button => { button.textContent = t('preset', { days: button.dataset.days }); });
        updateOperation();
        refreshCountdowns();
    }
    window.addEventListener('adminui:languageChanged', translateControls);
    translateControls();
    window.setInterval(refreshCountdowns, 1000);
})();
