import { pluginId, escapeHtml, numberOr, setupTabs } from './configurationpage?name=CcSyncCommon.js';

const levelColors = { ok: '#52b54b', warn: '#e5a00d', error: '#cc3333' };

export default function (view) {
    const form = view.querySelector('#ccSyncSettingsForm');
    const probeResult = view.querySelector('#ccSyncProbeResult');
    const field = (id) => view.querySelector('#' + id);

    function load() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then((config) => {
            field('ClipCornDirectory').value = config.ClipCornDirectory || '';
            field('ClipCornTimeZone').value = config.ClipCornTimeZone || '';
            field('DateOnlyTimeOfDay').value = config.DateOnlyTimeOfDay || '12:00';
            field('LockStaleMinutes').value = config.LockStaleMinutes;
            field('BackupCount').value = config.BackupCount;
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
