import { escapeHtml, formatTimestamp, apiGet, setupTabs } from './configurationpage?name=CcSyncCommon.js';

const refreshMs = 10000;
const previewRows = 500;
const levelColors = { ok: '#52b54b', warn: '#e5a00d', error: '#cc3333' };
const outcomeColors = { applied: '#52b54b', 'nothing-to-do': '#52b54b', 'dry-run': '#e5a00d', guarded: '#e5a00d', waiting: '#e5a00d', running: 'inherit', aborted: '#cc3333' };

const matchLists = [
    ['UnmatchedJf', 'Jellyfin items without a match'],
    ['Ambiguous', 'Ambiguous fallbacks (skipped)'],
    ['KindMismatches', 'Kind mismatches (skipped)'],
    ['FallbackMatched', 'Matched by fallback (NFO missing or outdated?)'],
    ['CcProblems', 'jClipCorn elements with unreadable values (skipped)'],
    ['MatchedSeveralTimes', 'jClipCorn elements matching several Jellyfin items'],
    ['UnmatchedCc', 'jClipCorn elements without a Jellyfin item']
];

function line(label, value, color) {
    return '<div style="margin: .25em 0;"><strong>' + escapeHtml(label) + ':</strong> '
        + (color ? '<span style="color: ' + color + ';">' + value + '</span>' : value) + '</div>';
}

function summaryText(json) {
    try {
        const s = JSON.parse(json || '{}');
        return Object.keys(s).filter((k) => s[k] !== null && s[k] !== '' && s[k] !== 0)
            .map((k) => k + ' ' + s[k]).join(', ');
    } catch (e) {
        return '';
    }
}

function runLine(label, run) {
    if (!run) {
        return line(label, 'never');
    }
    return line(label, escapeHtml(run.Outcome) + ' — ' + escapeHtml(formatTimestamp(run.StartedUtc))
        + (run.Summary && run.Summary !== '{}' ? ' <span class="fieldDescription">(' + escapeHtml(summaryText(run.Summary)) + ')</span>' : ''),
        outcomeColors[run.Outcome]);
}

function date(value) {
    return value ? new Date(value).toLocaleString() : '∅';
}

function changeCells(c) {
    const watched = [];
    if (c.Played) watched.push((c.Played.Old ? 'played' : 'unplayed') + ' → ' + (c.Played.New ? 'played' : 'unplayed'));
    if (c.PlayCount) watched.push(c.PlayCount.Old + '× → ' + c.PlayCount.New + '×');
    if (c.LastPlayed) watched.push(date(c.LastPlayed.Old) + ' → ' + date(c.LastPlayed.New));
    const favourite = c.Favorite ? (c.Favorite.Old ? '★' : '☆') + ' → ' + (c.Favorite.New ? '★' : '☆') : '';
    const tags = (c.AddTags || []).map((t) => '+' + t).concat((c.RemoveTags || []).map((t) => '−' + t));
    if (c.LockTags) tags.push('lock tags');
    return [watched.join('<br>'), favourite, escapeHtml(tags.join(' '))];
}

export default function (view) {
    const field = (id) => view.querySelector('#' + id);
    let timer = null;
    let previewId = null;

    function renderStatus(s) {
        field('ccStWarnings').innerHTML = (s.Warnings || []).map((w) =>
            '<div style="margin: .3em 0;"><strong style="color: ' + (levelColors[w.Level] || 'inherit') + ';">' + escapeHtml(w.Level.toUpperCase())
            + '</strong> <strong>' + escapeHtml(w.Name) + ':</strong> ' + escapeHtml(w.Message) + '</div>').join('');

        let health = line('Directory', escapeHtml(s.Directory || '—') + (s.DirectoryExists ? '' : ' (missing)'), s.DirectoryExists ? null : levelColors.error);
        [s.MainLock, s.HistoryLock].filter((l) => l).forEach((l) => {
            health += line('Lock', escapeHtml(l.Description), l.Held || l.Stale ? levelColors.warn : levelColors.ok);
        });
        if ((s.Journals || []).length) {
            health += line('Journal', escapeHtml(s.Journals.join(', ')) + ' — a transaction is running or was interrupted', levelColors.warn);
        }
        if (s.Match && s.Match.Database) {
            const db = s.Match.Database;
            health += line('Database', 'main schema ' + db.MainVersion + ', user data schema ' + db.UserVersion + ', ' + escapeHtml(db.MainDuuid)
                + ' <span class="fieldDescription">(as of the last sync)</span>');
        }
        field('ccStHealth').innerHTML = health;

        const counts = s.PendingCounts || {};
        let runs = runLine('Last sync', s.LastSync) + runLine('Last write', s.LastFlush);
        if (s.FlushWait) {
            runs += line('Writes waiting', escapeHtml(s.FlushWait.Reason) + ' — since ' + escapeHtml(formatTimestamp(s.FlushWait.SinceUtc)), levelColors.warn);
        }
        runs += line('Pending updates', (counts.pending || 0) + ' pending, ' + (counts.failed || 0) + ' failed'
            + ' <a href="#" class="ccStToPending" style="color: #00a4dc;">(Pending tab)</a>');
        field('ccStRuns').innerHTML = runs;
        field('ccStRuns').querySelectorAll('.ccStToPending').forEach((a) => a.addEventListener('click', (e) => {
            e.preventDefault();
            Dashboard.navigate('configurationpage?name=CcSyncPending');
        }));

        const m = s.Match;
        field('ccStMatch').innerHTML = !m ? '<p class="fieldDescription">No sync has run yet.</p>'
            : line('As of', escapeHtml(formatTimestamp(m.CreatedUtc)))
            + line('Matched', m.Matched + ' of ' + m.JfItems + ' Jellyfin items ('
                + Object.keys(m.MatchedByKind || {}).map((k) => m.MatchedByKind[k] + ' ' + k.toLowerCase()).join(', ') + '); '
                + m.CcElements + ' jClipCorn elements')
            + (m.TagWarnings || []).map((w) => line('Tag warning', escapeHtml(w), levelColors.warn)).join('');

        if (s.Preview) {
            if (previewId !== s.Preview.PlanId) {
                loadPreview();
            }
        } else {
            previewId = null;
            field('ccStPreviewSection').classList.add('hide');
        }

        if (m) {
            renderMatchListHeaders(m);
        }
    }

    function renderMatchListHeaders(m) {
        field('ccStMatchLists').innerHTML = matchLists.map(([key, label]) =>
            '<details class="ccStList" data-key="' + key + '" style="margin: .4em 0;"' + (m[key] ? '' : ' hidden') + '>'
            + '<summary style="cursor: pointer;">' + escapeHtml(label) + ': ' + (m[key] || 0) + '</summary>'
            + '<div class="ccStListBody fieldDescription">loading…</div></details>').join('');
        field('ccStMatchLists').querySelectorAll('.ccStList').forEach((d) => d.addEventListener('toggle', () => {
            if (d.open) {
                loadMatchList(d);
            }
        }));
    }

    function loadMatchList(details) {
        apiGet('CcSync/Matching').then((report) => {
            const list = report[details.getAttribute('data-key')] || { Total: 0, Items: [] };
            details.querySelector('.ccStListBody').innerHTML = '<ul>' + list.Items.map((e) =>
                '<li>' + escapeHtml(e.Kind) + ' <strong>' + escapeHtml(e.Name) + '</strong>'
                + (e.Reason ? ' — ' + escapeHtml(e.Reason) : '')
                + (e.Path ? '<br><code>' + escapeHtml(e.Path) + '</code>' : '') + '</li>').join('') + '</ul>'
                + (list.Total > list.Items.length ? '<p>… and ' + (list.Total - list.Items.length) + ' more</p>' : '');
        });
    }

    function loadPreview() {
        apiGet('CcSync/Sync/Preview').then((p) => {
            if (!p || !p.PlanId) {
                field('ccStPreviewSection').classList.add('hide');
                return;
            }
            previewId = p.PlanId;
            field('ccStPreviewSection').classList.remove('hide');
            field('ccStPreviewInfo').innerHTML = line('Reason', escapeHtml(p.Reason))
                + line('Planned', escapeHtml(formatTimestamp(p.CreatedUtc)) + ': ' + p.ItemsChanged + ' item(s) change, ' + p.Unwatches + ' un-watched')
                + '<p class="fieldDescription">Apply re-plans against the current Jellyfin state and is refused if jClipCorn or the pending updates changed since.</p>';
            const changes = p.Changes || [];
            field('ccStPreviewRows').innerHTML = changes.slice(0, previewRows).map((c) => {
                const cells = changeCells(c);
                return '<tr class="detailTableBodyRow detailTableBodyRow-shaded">'
                    + '<td class="detailTableBodyCell">' + escapeHtml(c.Name) + ' <span class="fieldDescription">(' + escapeHtml(c.Kind) + ')</span></td>'
                    + '<td class="detailTableBodyCell"' + (c.Played && c.Played.Old && !c.Played.New ? ' style="color: #cc3333;"' : '') + '>' + cells[0] + '</td>'
                    + '<td class="detailTableBodyCell">' + cells[1] + '</td>'
                    + '<td class="detailTableBodyCell">' + cells[2] + '</td></tr>';
            }).join('');
            field('ccStPreviewMore').textContent = changes.length > previewRows ? '… and ' + (changes.length - previewRows) + ' more (all are in the History tab as planned-* entries).' : '';
        });
    }

    function load() {
        return apiGet('CcSync/Status').then(renderStatus).catch((err) => {
            console.error('[CcSync] could not load the status', err);
        });
    }

    function post(url, message) {
        field('ccStActionResult').textContent = message;
        return ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl(url) });
    }

    function stop() {
        if (timer !== null) {
            clearInterval(timer);
            timer = null;
        }
    }

    view.addEventListener('viewshow', function () {
        setupTabs(view, 1);
        load();
        stop();
        timer = setInterval(load, refreshMs);
    });
    view.addEventListener('viewhide', stop);
    view.addEventListener('viewdestroy', stop);

    field('ccStRunSync').addEventListener('click', () => post('CcSync/Sync/Run', 'Sync queued…').then(() => setTimeout(load, 3000)));
    field('ccStFlush').addEventListener('click', () => post('CcSync/Flush/Run', 'Write queued…').then(() => setTimeout(load, 3000)));
    field('ccStPreview').addEventListener('click', () => {
        field('ccStActionResult').textContent = 'Planning…';
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('CcSync/Sync/Preview'), dataType: 'json' }).then((r) => {
            field('ccStActionResult').textContent = r.Outcome + ': ' + r.Message;
            previewId = null;
            load();
        }).catch(() => {
            field('ccStActionResult').textContent = 'Preview failed; see the server log.';
        });
    });
    field('ccStApply').addEventListener('click', () => {
        if (!previewId || !window.confirm('Apply the planned changes to Jellyfin now?')) {
            return;
        }
        post('CcSync/Sync/Apply?planId=' + encodeURIComponent(previewId), 'Applying in the background…')
            .then(() => setTimeout(load, 3000))
            .catch(() => {
                field('ccStActionResult').textContent = 'The preview changed meanwhile; reload the page.';
                load();
            });
    });
}
