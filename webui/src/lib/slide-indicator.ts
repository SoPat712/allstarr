/** Moves one indicator between a container's selected items, so selection slides instead of jumping. */
export function slideIndicator(container: HTMLElement, selector: string) {
  const indicator = document.createElement("span");
  indicator.className = "slide-indicator";
  indicator.setAttribute("aria-hidden", "true");
  indicator.setAttribute("role", "presentation");
  container.append(indicator);
  container.dataset.indicator = "";

  let frame = 0;
  const place = () => {
    frame = 0;
    const selected = container.querySelector<HTMLElement>(selector);
    indicator.hidden = !selected || selected.offsetWidth === 0;
    if (!selected) return;
    indicator.style.setProperty("--x", `${selected.offsetLeft}px`);
    indicator.style.setProperty("--y", `${selected.offsetTop}px`);
    indicator.style.setProperty("--w", `${selected.offsetWidth}px`);
    indicator.style.setProperty("--h", `${selected.offsetHeight}px`);
  };
  const schedule = () => { frame ||= requestAnimationFrame(place); };
  place();
  const ready = requestAnimationFrame(() => requestAnimationFrame(() => { indicator.dataset.ready = ""; }));

  const changes = new MutationObserver(schedule);
  changes.observe(container, { subtree: true, childList: true, attributes: true, attributeFilter: ["class", "aria-current", "aria-selected"] });
  const sizes = new ResizeObserver(schedule);
  sizes.observe(container);
  void document.fonts?.ready.then(schedule);

  return {
    destroy() {
      changes.disconnect();
      sizes.disconnect();
      cancelAnimationFrame(frame);
      cancelAnimationFrame(ready);
      indicator.remove();
    },
  };
}
