export function initScrobblingView({
  isAuthenticated,
  loadScrobblingConfig,
} = {}) {
  const canLoad =
    typeof isAuthenticated === "function" ? isAuthenticated : () => false;
  const load =
    typeof loadScrobblingConfig === "function"
      ? loadScrobblingConfig
      : () => window.loadScrobblingConfig?.();

  function onActivateScrobbling() {
    if (canLoad()) {
      load();
    }
  }

  const scrobblingTab = document.querySelector('.tab[data-tab="scrobbling"]');
  if (scrobblingTab) {
    scrobblingTab.addEventListener("click", onActivateScrobbling);
  }

  const scrobblingSidebar = document.querySelector(
    '.sidebar-link[data-tab="scrobbling"]',
  );
  if (scrobblingSidebar) {
    scrobblingSidebar.addEventListener("click", onActivateScrobbling);
  }
}

