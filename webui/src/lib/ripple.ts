const targets = [
  '[data-slot="button"]', ".icon-button", ".auth-submit", ".filter-chip", ".add-chip",
  ".desktop-navigation a", ".mobile-navigation a", ".mobile-navigation button",
  ".segmented-tabs a", ".segmented-tabs button", ".settings-nav > a",
].join(",");

/** Adds a pressed-state ripple to controls across the app, from one listener. */
export function installRipple() {
  const reduced = matchMedia("(prefers-reduced-motion: reduce)");
  const press = (event: PointerEvent) => {
    if (reduced.matches || event.button !== 0) return;
    const target = (event.target as Element | null)?.closest<HTMLElement>(targets);
    if (!target || target.matches(":disabled, [aria-disabled='true']")) return;
    const bounds = target.getBoundingClientRect();
    const ripple = document.createElement("span");
    ripple.className = "ripple";
    ripple.setAttribute("aria-hidden", "true");
    // The ripple fills its control and is revealed by a growing circle, so it never paints outside it.
    ripple.style.setProperty("--ripple-at", `${event.clientX - bounds.left}px ${event.clientY - bounds.top}px`);
    ripple.style.setProperty("--ripple-size", `${Math.hypot(bounds.width, bounds.height)}px`);
    ripple.addEventListener("animationend", () => ripple.remove(), { once: true });
    target.append(ripple);
  };
  document.addEventListener("pointerdown", press, { passive: true });
  return () => document.removeEventListener("pointerdown", press);
}
