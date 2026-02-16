// Utility functions

export function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

export function escapeJs(text) {
    return text.replace(/'/g, "\\'").replace(/"/g, '\\"').replace(/\n/g, '\\n');
}

export function showToast(message, type = 'success', duration = 3000) {
    const toast = document.createElement('div');
    toast.className = 'toast ' + type;
    toast.textContent = message;
    document.body.appendChild(toast);
    setTimeout(() => toast.remove(), duration);
}

export function formatCookieAge(setDateStr, hasCookie = false) {
    if (!setDateStr) {
        if (hasCookie) {
            return { text: 'Unknown age', class: 'warning', detail: 'Cookie date not tracked', needsInit: true };
        }
        return { text: 'No cookie', class: '', detail: '', needsInit: false };
    }
    
    const setDate = new Date(setDateStr);
    const now = new Date();
    const diffMs = now - setDate;
    const daysAgo = Math.floor(diffMs / (1000 * 60 * 60 * 24));
    const monthsAgo = daysAgo / 30;
    
    let status = 'success';  // green: < 6 months
    if (monthsAgo >= 10) status = 'error';  // red: > 10 months
    else if (monthsAgo >= 6) status = 'warning';  // yellow: 6-10 months
    
    let text;
    if (daysAgo === 0) text = 'Set today';
    else if (daysAgo === 1) text = 'Set yesterday';
    else if (daysAgo < 30) text = `Set ${daysAgo} days ago`;
    else if (daysAgo < 60) text = 'Set ~1 month ago';
    else text = `Set ~${Math.floor(monthsAgo)} months ago`;
    
    const remaining = 12 - monthsAgo;
    let detail;
    if (remaining > 6) detail = 'Cookie typically lasts ~1 year';
    else if (remaining > 2) detail = `~${Math.floor(remaining)} months until expiration`;
    else if (remaining > 0) detail = 'Cookie may expire soon!';
    else detail = 'Cookie may have expired - update if having issues';
    
    return { text, class: status, detail, needsInit: false };
}

export function capitalizeProvider(provider) {
    const providerMap = {
        'squidwtf': 'SquidWTF',
        'deezer': 'Deezer',
        'qobuz': 'Qobuz'
    };
    return providerMap[provider?.toLowerCase()] || provider;
}
