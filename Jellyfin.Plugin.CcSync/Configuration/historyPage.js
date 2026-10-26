import { escapeHtml, formatTimestamp, apiGet, apiSend, setupTabs } from './configurationpage?name=CcSyncCommon.js';

const pageSize = 100;

const kinds = [
    'jf-played', 'jf-favorite', 'jf-tag-added', 'jf-tag-removed',
    'pending-created', 'pending-written', 'pending-duplicate', 'pending-failed', 'pending-discarded', 'pending-retry',
    'cc-locked', 'run-sync', 'run-flush', 'planned-*'
];

function localDateToIso(value, addDays) {
    if (!value) {
        return undefined;
    }
    const [y, m, d] = value.split('-').map(Number);
    return new Date(y, m - 1, d + addDays).toISOString();
}

export default function (view) {
    const field = (id) => view.querySelector('#' + id);
    const rows = field('ccHistRows');
    let offset = 0;
    let total = 0;

    field('ccHistKind').innerHTML = '<option value="">All kinds</option>'
        + kinds.map((k) => '<option value="' + k + '">' + k + '</option>').join('');

    function query() {
        const q = { limit: pageSize, offset: offset };
        const kind = field('ccHistKind').value;
        const search = field('ccHistSearch').value.trim();
        const run = field('ccHistRun').value.trim();
        const from = localDateToIso(field('ccHistFrom').value, 0);
        const to = localDateToIso(field('ccHistTo').value, 1);
        if (kind) q.kind = kind;
        if (search) q.search = search;
        if (run) q.runId = run;
        if (from) q.from = from;
        if (to) q.to = to;
        return q;
    }

    function render(page) {
        total = page.Total || 0;
        const entries = page.Entries || [];
        rows.innerHTML = entries.map((e) => {
            const color = e.Success ? '' : ' style="color: #cc3333;"';
            const isRun = e.Kind === 'run-sync' || e.Kind === 'run-flush';
            const kindCell = isRun && e.RunId
                ? '<a href="#" class="ccHistRunLink" data-run="' + escapeHtml(e.RunId) + '">' + escapeHtml(e.Kind) + '</a>'
                : escapeHtml(e.Kind);
            const change = (e.OldValue != null || e.NewValue != null)
                ? escapeHtml(e.OldValue == null ? '∅' : e.OldValue) + ' → ' + escapeHtml(e.NewValue == null ? '∅' : e.NewValue)
                : '';
            return '<tr class="detailTableBodyRow detailTableBodyRow-shaded">'
                + '<td class="detailTableBodyCell" style="white-space: nowrap;">' + escapeHtml(formatTimestamp(e.TsUtc)) + '</td>'
                + '<td class="detailTableBodyCell"' + color + '>' + kindCell + '</td>'
                + '<td class="detailTableBodyCell">' + escapeHtml(e.ItemName || '') + (e.ItemKind ? ' <span class="fieldDescription">(' + escapeHtml(e.ItemKind) + ')</span>' : '') + '</td>'
                + '<td class="detailTableBodyCell">' + escapeHtml(e.Field || '') + '</td>'
                + '<td class="detailTableBodyCell">' + change + '</td>'
                + '<td class="detailTableBodyCell">' + escapeHtml(e.Detail || '') + '</td>'
                + '</tr>';
        }).join('');

        field('ccHistEmpty').classList.toggle('hide', entries.length > 0);
        field('ccHistRange').textContent = total === 0 ? '' : (offset + 1) + '–' + (offset + entries.length) + ' of ' + total;
        field('ccHistPrev').disabled = offset === 0;
        field('ccHistNext').disabled = offset + entries.length >= total;

        rows.querySelectorAll('.ccHistRunLink').forEach((a) => a.addEventListener('click', (ev) => {
            ev.preventDefault();
            field('ccHistKind').value = '';
            field('ccHistRun').value = a.getAttribute('data-run');
            offset = 0;
            load();
        }));
    }

    function load() {
        return apiGet('CcSync/History', query()).then(render).catch((err) => {
            console.error('[CcSync] could not load the history', err);
            render({ Total: 0, Entries: [] });
        });
    }

    view.addEventListener('viewshow', function () {
        setupTabs(view, 3);
        load();
    });

    field('ccHistApply').addEventListener('click', () => { offset = 0; load(); });
    field('ccHistReset').addEventListener('click', () => {
        ['ccHistKind', 'ccHistSearch', 'ccHistRun', 'ccHistFrom', 'ccHistTo'].forEach((id) => { field(id).value = ''; });
        offset = 0;
        load();
    });
    field('ccHistSearch').addEventListener('keydown', (ev) => {
        if (ev.key === 'Enter') { offset = 0; load(); }
    });
    field('ccHistPrev').addEventListener('click', () => { offset = Math.max(0, offset - pageSize); load(); });
    field('ccHistNext').addEventListener('click', () => { if (offset + pageSize < total) { offset += pageSize; load(); } });

    field('ccHistPrune').addEventListener('click', () => {
        const days = Number(field('ccHistPruneDays').value);
        if (Number.isNaN(days) || days < 0) {
            return;
        }
        apiSend('DELETE', 'CcSync/History', { olderThanDays: days }).then((r) => {
            field('ccHistPruneResult').textContent = (r && r.Deleted) + ' entries deleted.';
            offset = 0;
            load();
        }).catch((err) => {
            console.error('[CcSync] prune failed', err);
            field('ccHistPruneResult').textContent = 'Pruning failed.';
        });
    });
}
