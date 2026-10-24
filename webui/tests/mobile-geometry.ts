import { expect, type Page } from "@playwright/test";

export async function expectContainedMobileGeometry(page: Page) {
  const audit = await page.evaluate(() => {
    const visible = (element: HTMLElement) => {
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return style.display !== "none" && style.visibility !== "hidden" &&
        rect.width > 0 && rect.height > 0;
    };
    const identify = (element: HTMLElement) => {
      const role = element.getAttribute("role");
      const classes = [...element.classList].slice(0, 3).join(".");
      const text = element.textContent?.replace(/\s+/g, " ").trim().slice(0, 36);
      return `${element.tagName.toLowerCase()}${element.id ? `#${element.id}` : ""}${classes ? `.${classes}` : ""}${role ? `[role=${role}]` : ""}${text ? ` “${text}”` : ""}`;
    };
    const hasLocalScrollOwner = (element: HTMLElement) => {
      let parent = element.parentElement;
      while (parent && parent !== document.body) {
        const style = getComputedStyle(parent);
        if (/(auto|scroll)/.test(style.overflowX) && parent.scrollWidth > parent.clientWidth + 1)
          return true;
        parent = parent.parentElement;
      }
      return false;
    };
    const viewportEscapes: string[] = [];
    for (const element of document.querySelectorAll<HTMLElement>("body *")) {
      if (!visible(element)) continue;
      const rect = element.getBoundingClientRect();
      if (rect.left >= -1 && rect.right <= window.innerWidth + 1) continue;
      if (hasLocalScrollOwner(element)) continue;
      viewportEscapes.push(`${identify(element)} [${Math.round(rect.left)}, ${Math.round(rect.right)}]`);
      if (viewportEscapes.length === 12) break;
    }

    const groupEscapes: string[] = [];
    const groups = document.querySelectorAll<HTMLElement>(
      ".mobile-navigation, .dialog-actions, .panel-heading-actions, .sources-heading-actions, .playlist-toolbar-actions, [role=tablist]",
    );
    for (const group of groups) {
      if (!visible(group)) continue;
      const groupRect = group.getBoundingClientRect();
      const scrollable = /(auto|scroll)/.test(getComputedStyle(group).overflowX) &&
        group.scrollWidth > group.clientWidth + 1;
      const controls = group.querySelectorAll<HTMLElement>("a, button, [role=tab]");
      for (const control of controls) {
        if (!visible(control) || control.closest(".mobile-navigation, .dialog-actions, .panel-heading-actions, .sources-heading-actions, .playlist-toolbar-actions, [role=tablist]") !== group)
          continue;
        if (scrollable && control.getAttribute("aria-selected") !== "true") continue;
        const rect = control.getBoundingClientRect();
        if (rect.left < groupRect.left - 1 || rect.right > groupRect.right + 1)
          groupEscapes.push(
            `${identify(control)} [${Math.round(rect.left)}, ${Math.round(rect.right)}] outside ${identify(group)} ` +
              `[${Math.round(groupRect.left)}, ${Math.round(groupRect.right)}], scroll ${Math.round(group.scrollLeft)}/${group.scrollWidth - group.clientWidth}`,
          );
      }
    }

    const undersizedTargets: string[] = [];
    const targets = new Set(document.querySelectorAll<HTMLElement>(
      "button, [role=button], [role=tab], summary, [data-slot=button], .mobile-navigation a",
    ));
    for (const target of targets) {
      if (!visible(target) || target.matches(":disabled")) continue;
      const rect = target.getBoundingClientRect();
      if (rect.width < 43 || rect.height < 43)
        undersizedTargets.push(`${identify(target)} ${Math.round(rect.width)}×${Math.round(rect.height)}`);
      if (undersizedTargets.length === 12) break;
    }
    const navReserveErrors: string[] = [];
    const mobileNav = document.querySelector<HTMLElement>(".mobile-navigation");
    const workspace = document.querySelector<HTMLElement>(".workspace");
    if (mobileNav && workspace && visible(mobileNav)) {
      const navHeight = mobileNav.getBoundingClientRect().height;
      const reserved = Number.parseFloat(getComputedStyle(workspace).paddingBottom);
      if (reserved + 1 < navHeight)
        navReserveErrors.push(`workspace reserves ${Math.round(reserved)}px for a ${Math.round(navHeight)}px navigation bar`);
    }
    return { viewportEscapes, groupEscapes, undersizedTargets, navReserveErrors };
  });
  expect(audit.viewportEscapes, "visible descendants must stay in the viewport or a local horizontal scroller").toEqual([]);
  expect(audit.groupEscapes, "controls must stay inside their navigation/action outline").toEqual([]);
  expect(audit.undersizedTargets, "touch controls must provide a 44px target").toEqual([]);
  expect(audit.navReserveErrors, "fixed mobile navigation must have matching content clearance").toEqual([]);
}
