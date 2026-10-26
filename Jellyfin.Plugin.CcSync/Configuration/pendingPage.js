import { escapeHtml, formatTimestamp, apiGet, setupTabs } from './configurationpage?name=CcSyncCommon.js';

const pageSize = 100;
const statusColors = { pending: '#e5a00d', written: '#52b54b', duplicate: '#52b54b', failed: '#cc3333', discarded: 'inherit' };

export default function (view) {
    const field = (id) => view.querySelector('#' + id);
    const rows = field('ccPendRows');
    let offset = 0;
    let total = 0;

    function action(type, url) {
        return ApiClient.ajax({ type: type, url: ApiClient.getUrl(url) }).then(load).catch((err) => {
            console.error('[CcSync] pending action failed', err);
            load();
        });
    }

    function render(response) {
        total = response.Total || 0;
        const items = response.Items || [];
        const counts = response.Counts || {};
        field('ccPendCounts').textContent = Object.keys(counts).length === 0
            ? 'No watches recorded yet.'
            : Object.keys(counts).sort().map((k) => counts[k] + ' ' + k).join(', ');

        rows.innerHTML = items.map((p) => {
            const buttons = (p.Status === 'failed'
                ? '<button is="emby-button" class="raised ccPendRetry" data-id="' + p.Id + '"><span>Retry</span></button>' : '')
                + (p.Status === 'failed' || p.Status === 'pending'
                    ? '<button is="emby-button" class="raised ccPendDiscard" data-id="' + p.Id + '"><span>Discard</span></button>' : '');
            const last = p.LastError ? escapeHtml(p.LastError) : (p.CcEntry ? 'entry ' + escapeHtml(p.CcEntry) : '');
            return '<tr class="detailTableBodyRow detailTableBodyRow-shaded">'
                + '<td class="detailTableBodyCell">' + p.Id + '</td>'
                + '<td class="detailTableBodyCell" style="white-space: nowrap;">' + escapeHtml(formatTimestamp(p.CreatedUtc)) + '</td>'
                + '<td class="detailTableBodyCell">' + escapeHtml(p.ItemName) + ' <span class="fieldDescription">(' + escapeHtml(p.ItemKind) + ')</span></td>'
                + '<td class="detailTableBodyCell" style="white-space: nowrap;">' + escapeHtml(formatTimestamp(p.WatchedAtUtc)) + '</td>'
                + '<td class="detailTableBodyCell">' + escapeHtml(p.Source) + '</td>'
                + '<td class="detailTableBodyCell" style="color: ' + (statusColors[p.Status] || 'inherit') + ';">' + escapeHtml(p.Status) + '</td>'
                + '<td class="detailTableBodyCell">' + p.Attempts + '</td>'
                + '<td class="detailTableBodyCell">' + last + '</td>'
                + '<td class="detailTableBodyCell" style="white-space: nowrap;">' + buttons + '</td>'
                + '</tr>';
        }).join('');

        field('ccPendEmpty').classList.toggle('hide', items.length > 0);
        field('ccPendRange').textContent = total === 0 ? '' : (offset + 1) + '–' + (offset + items.length) + ' of ' + total;
        field('ccPendPrev').disabled = offset === 0;
        field('ccPendNext').disabled = offset + items.length >= total;

        rows.querySelectorAll('.ccPendRetry').forEach((b) => b.addEventListener('click', () =>
            action('POST', 'CcSync/Pending/' + b.getAttribute('data-id') + '/Retry')));
        rows.querySelectorAll('.ccPendDiscard').forEach((b) => b.addEventListener('click', () => {
            if (window.confirm('Discard this watch? It will never be written to jClipCorn, and the next sync reverts it in Jellyfin.')) {
                action('DELETE', 'CcSync/Pending/' + b.getAttribute('data-id'));
            }
        }));
    }

    function load() {
        const q = { limit: pageSize, offset: offset };
        if (field('ccPendStatus').value) {
            q.status = field('ccPendStatus').value;
        }
        return apiGet('CcSync/Pending', q).then(render).catch((err) => {
            console.error('[CcSync] could not load pending updates', err);
            render({ Total: 0, Items: [], Counts: {} });
        });
    }

    view.addEventListener('viewshow', function () {
        setupTabs(view, 2);
        load();
    });

    field('ccPendStatus').addEventListener('change', () => { offset = 0; load(); });
    field('ccPendRefresh').addEventListener('click', load);
    field('ccPendPrev').addEventListener('click', () => { offset = Math.max(0, offset - pageSize); load(); });
    field('ccPendNext').addEventListener('click', () => { if (offset + pageSize < total) { offset += pageSize; load(); } });
}
