import { pluginId, setupTabs } from './configurationpage?name=CcSyncCommon.js';

export default function (view) {
    const form = view.querySelector('#ccSyncSettingsForm');

    function save() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then((config) => {
            ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });
    }

    view.addEventListener('viewshow', function () {
        setupTabs(view, 0);
    });

    form.addEventListener('submit', (e) => {
        e.preventDefault();
        save();
        return false;
    });
}
