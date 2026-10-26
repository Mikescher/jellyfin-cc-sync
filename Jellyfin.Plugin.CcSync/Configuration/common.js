export const pluginId = '7c59bc79-ad24-4106-94b9-d13477e9a518';

const tabs = [
    { href: 'configurationpage?name=CcSync', name: 'Settings' },
    { href: 'configurationpage?name=CcSyncStatus', name: 'Status' },
    { href: 'configurationpage?name=CcSyncPending', name: 'Pending' },
    { href: 'configurationpage?name=CcSyncHistory', name: 'History' }
];

export function escapeHtml(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[c]);
}

export function formatTimestamp(value) {
    if (!value) {
        return '';
    }
    const date = new Date(value);
    return isNaN(date.getTime()) ? String(value) : date.toLocaleString();
}

export function numberOr(element, fallback) {
    const value = Number(element.value);
    return element.value.trim() === '' || Number.isNaN(value) ? fallback : value;
}

export function apiGet(path, query) {
    return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(path, query), dataType: 'json' });
}

export function apiSend(type, path, query) {
    return ApiClient.ajax({ type: type, url: ApiClient.getUrl(path, query), dataType: 'json' });
}

// Navigation is a nice-to-have, so every failure here is swallowed: an exception
// escaping this would abort the viewshow handler and leave the page unpopulated.
export function setupTabs(view, selectedIndex) {
    try {
        if (window.LibraryMenu && typeof window.LibraryMenu.setTabs === 'function') {
            window.LibraryMenu.setTabs('ccsync', selectedIndex, () => tabs);
            return;
        }
    } catch (err) {
        console.error('[CcSync] LibraryMenu.setTabs failed', err);
    }

    const container = view.querySelector('.content-primary');
    if (!container || container.querySelector('.ccSyncTabFallback')) {
        return;
    }

    const bar = document.createElement('div');
    bar.className = 'ccSyncTabFallback';
    bar.style.marginBottom = '1em';
    bar.innerHTML = tabs.map((tab, index) => index === selectedIndex
        ? '<strong style="margin-right: 1em;">' + escapeHtml(tab.name) + '</strong>'
        : '<a style="margin-right: 1em;" href="' + escapeHtml(tab.href) + '">' + escapeHtml(tab.name) + '</a>').join('');
    container.insertBefore(bar, container.firstChild);
}
