import { setupTabs } from './configurationpage?name=CcSyncCommon.js';

export default function (view) {
    view.addEventListener('viewshow', function () {
        setupTabs(view, 1);
    });
}
