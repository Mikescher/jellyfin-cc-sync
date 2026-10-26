import { pluginId, escapeHtml, numberOr, setupTabs } from './configurationpage?name=CcSyncCommon.js';

const levelColors = { ok: '#52b54b', warn: '#e5a00d', error: '#cc3333' };

// Stored score values in rank order; the stored value is not the rank (see docs/clipcorn-db.md).
const scores = [
    [5, 'Masterpiece'], [4, 'Favourite'], [3, 'Recommended'], [7, 'Average'],
    [2, 'Not recommended'], [1, 'Bad'], [0, 'Crap']
];

const checkboxes = [
    'WriteBackEnabled', 'ManualMarkCreatesPending', 'ResetWatchLaterOnWatch',
    'SyncEnabled', 'SyncPlayed', 'SyncPlayCount', 'SyncLastPlayed', 'SyncFavorites',
    'RatingTagsEnabled', 'GroupTagsEnabled', 'GroupTagsIncludeParents',
    'ApplyRatingsToMovies', 'ApplyRatingsToSeries', 'ApplyRatingsToSeasons', 'ApplyRatingsToEpisodes',
    'LockTagsField', 'DryRun', 'WriteToActivityLog'
];

const numbers = {
    WatchedThreshold: 0.9, DedupMinutes: 60, StartCoalesceMinutes: 10,
    MaxChangesPerRun: 100, MaxUnwatchPerRun: 20, HistoryRetentionDays: 0
};

// Jellyfin renders ids with or without dashes depending on the endpoint.
function normalizeId(id) {
    return String(id || '').replace(/-/g, '').toLowerCase();
}

function renderUsers(select, users, selected) {
    select.innerHTML = '<option value="">— none —</option>' + users.map((u) =>
        '<option value="' + escapeHtml(u.Id) + '">' + escapeHtml(u.Name) + '</option>').join('');
    const match = users.find((u) => normalizeId(u.Id) === normalizeId(selected));
    select.value = match ? match.Id : '';
}

function renderLibraries(container, libraries, selected) {
    const chosen = new Set((selected || []).map(normalizeId));
    container.innerHTML = libraries.map((lib) =>
        '<label class="checkboxContainer">'
        + '<input is="emby-checkbox" type="checkbox" class="ccSyncLibrary" data-id="' + escapeHtml(lib.ItemId) + '"'
        + (chosen.has(normalizeId(lib.ItemId)) ? ' checked' : '') + ' />'
        + '<span>' + escapeHtml(lib.Name) + ' <span class="fieldDescription">(' + escapeHtml(lib.CollectionType || 'mixed') + ')</span></span>'
        + '</label>').join('');
}

export default function (view) {
    const form = view.querySelector('#ccSyncSettingsForm');
    const probeResult = view.querySelector('#ccSyncProbeResult');
    const field = (id) => view.querySelector('#' + id);

    function load() {
        Dashboard.showLoadingMsg();
        Promise.all([
            ApiClient.getPluginConfiguration(pluginId),
            ApiClient.getUsers(),
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Library/VirtualFolders'), dataType: 'json' })
        ]).then(([config, users, libraries]) => {
            field('ClipCornDirectory').value = config.ClipCornDirectory || '';
            field('ClipCornTimeZone').value = config.ClipCornTimeZone || '';
            field('DateOnlyTimeOfDay').value = config.DateOnlyTimeOfDay || '12:00';
            field('LockStaleMinutes').value = config.LockStaleMinutes;
            field('BackupCount').value = config.BackupCount;

            renderUsers(field('SyncUserId'), users, config.SyncUserId);
            renderLibraries(view.querySelector('#ccSyncLibraries'), libraries || [], config.LibraryIds);

            checkboxes.forEach((id) => { field(id).checked = !!config[id]; });
            Object.keys(numbers).forEach((id) => { field(id).value = config[id]; });

            const favourite = new Set(config.FavoriteScores || []);
            view.querySelector('#ccSyncFavoriteScores').innerHTML = scores.map(([value, label]) =>
                '<label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" class="ccSyncScore" data-score="' + value + '"'
                + (favourite.has(value) ? ' checked' : '') + ' /><span>' + escapeHtml(label) + '</span></label>').join('');
            Dashboard.hideLoadingMsg();
        }).catch((err) => {
            console.error('[CcSync] could not load the configuration', err);
            Dashboard.hideLoadingMsg();
        });
    }

    function save() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then((config) => {
            config.ClipCornDirectory = field('ClipCornDirectory').value.trim();
            config.ClipCornTimeZone = field('ClipCornTimeZone').value.trim();
            config.DateOnlyTimeOfDay = field('DateOnlyTimeOfDay').value || '12:00';
            config.LockStaleMinutes = numberOr(field('LockStaleMinutes'), 180);
            config.BackupCount = numberOr(field('BackupCount'), 20);

            config.SyncUserId = field('SyncUserId').value;
            config.LibraryIds = Array.from(view.querySelectorAll('.ccSyncLibrary'))
                .filter((el) => el.checked)
                .map((el) => el.getAttribute('data-id'));

            checkboxes.forEach((id) => { config[id] = field(id).checked; });
            Object.keys(numbers).forEach((id) => { config[id] = numberOr(field(id), numbers[id]); });
            config.FavoriteScores = Array.from(view.querySelectorAll('.ccSyncScore'))
                .filter((el) => el.checked)
                .map((el) => Number(el.getAttribute('data-score')));

            ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });
    }

    function renderProbe(response) {
        const checks = (response && response.Checks) || [];
        probeResult.innerHTML = checks.map((check) =>
            '<div style="margin: .3em 0;">'
            + '<strong style="color: ' + (levelColors[check.Level] || 'inherit') + ';">' + escapeHtml(check.Level.toUpperCase()) + '</strong> '
            + '<strong>' + escapeHtml(check.Name) + ':</strong> ' + escapeHtml(check.Message)
            + '</div>').join('');
    }

    function probe() {
        probeResult.textContent = 'Testing…';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('CcSync/Probe'),
            data: JSON.stringify({
                Directory: field('ClipCornDirectory').value.trim(),
                LockStaleMinutes: numberOr(field('LockStaleMinutes'), 180),
                TimeZone: field('ClipCornTimeZone').value.trim()
            }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(renderProbe).catch((err) => {
            console.error('[CcSync] probe failed', err);
            probeResult.textContent = 'The test request failed; see the server log.';
        });
    }

    view.addEventListener('viewshow', function () {
        load();
        setupTabs(view, 0);
    });

    view.querySelector('#ccSyncProbe').addEventListener('click', probe);

    form.addEventListener('submit', (e) => {
        e.preventDefault();
        save();
        return false;
    });
}
