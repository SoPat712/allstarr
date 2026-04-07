export function initNavigationView({ switchTab } = {}) {
  const doSwitch =
    typeof switchTab === "function" ? switchTab : (tab) => window.switchTab?.(tab);

  document.querySelectorAll(".tab").forEach((tab) => {
    tab.addEventListener("click", () => {
      doSwitch(tab.dataset.tab);
    });
  });

  document.querySelectorAll(".sidebar-link").forEach((link) => {
    link.addEventListener("click", () => {
      doSwitch(link.dataset.tab);
    });
  });

  const hash = window.location.hash.substring(1);
  if (hash) {
    doSwitch(hash);
  }
}

