// Runs in the browser; selectors remain reproducible without changing application markup.
export function inspectDom(stateOnly = false) {
  const selector = (element: Element): string => {
    if (element.id && !/^(bits-|svelte-|radix-|:)/.test(element.id)) return `#${CSS.escape(element.id)}`;
    if (element === document.body) return "body";
    const siblings = [...element.parentElement!.children].filter(item => item.tagName === element.tagName);
    return `${selector(element.parentElement!)} > ${element.tagName.toLowerCase()}:nth-of-type(${siblings.indexOf(element) + 1})`;
  };
  const visible = (element: Element) => {
    const box = element.getBoundingClientRect(), style = getComputedStyle(element);
    return box.width > 0 && box.height > 0 && style.visibility === "visible" && style.display !== "none";
  };
  const name = (element: Element) => element.getAttribute("aria-label") ||
    (element.getAttribute("aria-labelledby") || "").split(/\s+/).map(id => document.getElementById(id)?.textContent || "").join(" ").trim() ||
    (element as HTMLInputElement).labels?.[0]?.textContent?.trim() || element.textContent?.replace(/\s+/g, " ").trim() || element.getAttribute("title") || "";
  const interactive = 'a[href],button,input,select,textarea,summary,[role=button],[role=tab],[role=switch],[aria-haspopup],[tabindex]';
  const controls = [...document.querySelectorAll<HTMLElement>(interactive)].filter(element => visible(element) &&
    !element.matches(':disabled,[aria-disabled=true]') && !element.closest('[inert],[aria-hidden=true]')).map(element => ({
    selector: selector(element), name: name(element), role: element.getAttribute("role") ||
      ({ A: "link", BUTTON: "button", INPUT: "input", SELECT: "combobox", TEXTAREA: "textbox", SUMMARY: "button" }[element.tagName] || "generic"),
    href: element.getAttribute("href"), tabIndex: element.tabIndex,
    selected: element.getAttribute("aria-selected"), popup: element.getAttribute("aria-haspopup"),
    overlay: element.closest('[role=dialog],[role=alertdialog],dialog[open],.side-panel') ? selector(element.closest('[role=dialog],[role=alertdialog],dialog[open],.side-panel')!) : null,
  }));
  const dialogs = [...document.querySelectorAll<HTMLElement>('[role=dialog],[role=alertdialog],dialog[open],.side-panel')].filter(visible);
  const activeTabs = controls.filter(item => item.selected === "true").map(item => item.name);
  const signature = JSON.stringify([location.hash, activeTabs, dialogs.map(name)]);
  if (stateOnly) return { controls, signature, samples: [] as Sample[], dialogs: dialogs.map(selector), tokenCount: 0 };
  const rootTokens = new Set<string>();
  const readRules = (rules: CSSRuleList) => {
    for (const rule of rules) {
      if (rule instanceof CSSStyleRule && rule.selectorText.split(",").some(part => part.trim() === ":root"))
        for (const property of rule.style) if (property.startsWith("--")) rootTokens.add(property);
      if ("cssRules" in rule) readRules((rule as CSSGroupingRule).cssRules);
    }
  };
  for (const sheet of document.styleSheets) { try { readRules(sheet.cssRules); } catch { /* Cross-origin sheets cannot be inspected. */ } }
  const probe = document.createElement("span"); document.body.append(probe);
  const colors = new Set<string>(["rgba(0, 0, 0, 0)"]);
  const spaces = new Set<string>(["0px"]);
  for (const token of rootTokens) {
    const raw = getComputedStyle(document.documentElement).getPropertyValue(token).trim();
    if (raw && CSS.supports("color", raw)) { probe.style.color = raw; colors.add(getComputedStyle(probe).color); }
    if (token.startsWith("--space-")) { probe.style.width = `var(${token})`; spaces.add(getComputedStyle(probe).width); }
  }
  probe.remove();
  type Sample = { measurement: string; rule: string; selector: string; value: unknown; flagged: boolean; group?: string; count?: number };
  const samples: Sample[] = [];
  const add = (measurement: string, rule: string, element: Element, value: unknown, flagged = false, group?: string) =>
    samples.push({ measurement, rule, selector: selector(element), value, flagged, group });
  const properties = (style: CSSStyleDeclaration, names: string[]) => Object.fromEntries(names.map(property => [property, style.getPropertyValue(property)]));
  const elements = [...document.querySelectorAll<HTMLElement>("body *")].filter(visible);
  for (const element of elements) {
    const style = getComputedStyle(element);
    if (element.matches('button,[data-slot=button],[role=button]')) {
      const variant = element.dataset.variant || ((element.getAttribute("class") || "").match(/(?:^|\s)(?:btn|button)-(\w+)/)?.[1]) || "unclassified";
      for (const [property, value] of Object.entries(properties(style, ["height", "border-radius", "font-size", "font-weight", "padding", "background-color", "color"])))
        add("consistency", "button styles", element, value, false, `${element.dataset.slot || "native"}/${variant}/${element.dataset.size || "unspecified"}/${property}`);
    }
    if (element.matches("h1,h2,h3,.page-title"))
      for (const [property, value] of Object.entries(properties(style, ["font-size", "font-weight", "line-height"])))
        add("consistency", "heading styles", element, value, false, `${element.tagName.toLowerCase()}/${property}`);
    for (const property of ["color", "background-color", "border-top-color", "border-right-color", "border-bottom-color", "border-left-color"]) {
      if (property === "color" && ![...element.childNodes].some(node => node.nodeType === Node.TEXT_NODE && node.textContent?.trim())) continue;
      if (property.startsWith("border") && parseFloat(style.getPropertyValue(property.replace("color", "width"))) === 0) continue;
      const value = style.getPropertyValue(property);
      add("consistency", "colour tokens", element, value, !colors.has(value), property);
    }
    if (element.matches('section,article,[data-slot=card],[class*=card],[class*=row]'))
      for (const property of ["row-gap", "column-gap", "padding-top", "padding-right", "padding-bottom", "padding-left"]) {
        const value = style.getPropertyValue(property);
        add("consistency", "spacing tokens", element, value, value !== "normal" && !spaces.has(value), property);
      }
    if (element.matches("button,[role=tab],[data-slot=button]")) {
      const label = name(element);
      add("wording", "labels", element, label, /\.\.\./.test(label), element.getAttribute("role") === "tab" ? "tab" : "button");
      if (/\s+[A-Z][a-z]+/.test(label)) add("wording", "sentence case review", element, label, true);
    }
    if (element.matches("time") || element.childElementCount === 0 && /(?:\d{4}-\d{2}-\d{2}|\d{1,2}:\d{2}|\d+ (?:minutes?|hours?|days?) ago|\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\b)/.test(element.textContent || ""))
      add("wording", "date/time formats", element, element.textContent?.trim(), false,
        (element.textContent || "").replace(/\d+/g, "#").replace(/\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\b/g, "Month"));
  }
  const workspace = document.querySelector("main") || document.body;
  add("consistency", "one h1", workspace, document.querySelectorAll("h1").length, document.querySelectorAll("h1").length !== 1);
  const header = document.querySelector(".workspace-header") || workspace.querySelector("header") || workspace;
  add("consistency", "page header", header, { title: !!header.querySelector("h1,h2"), status: !!workspace.querySelector('.page-status'),
    tabs: !!workspace.querySelector('[role=tablist]') }, false, "structure");
  for (const dialog of dialogs) {
    const box = dialog.getBoundingClientRect(), style = getComputedStyle(dialog), heading = dialog.querySelector("header,.panel-heading");
    const close = dialog.querySelector('[aria-label*="Close"],[aria-label*="close"]')?.getBoundingClientRect();
    const actions = [...dialog.querySelectorAll<HTMLElement>('footer button,.dialog-actions button,.side-panel-footer button')].filter(visible);
    const primary = actions.find(item => item.dataset.variant === "default"), positions = actions.map(item => item.getBoundingClientRect().x);
    const primaryX = primary?.getBoundingClientRect().x;
    const layout = { width: Math.round(box.width), padding: style.padding,
      header: heading ? properties(getComputedStyle(heading), ["display", "align-items", "justify-content", "padding"]) : null,
      close: close ? { right: Math.round(box.right - close.right), top: Math.round(close.top - box.top) } : null,
      footer: actions.map(item => ({ label: name(item), variant: item.dataset.variant, x: Math.round(item.getBoundingClientRect().x) })),
      primarySide: !primary ? "none" : actions.length === 1 ? "only action" : primaryX === Math.max(...positions) ? "right" : primaryX === Math.min(...positions) ? "left" : "middle",
    };
    for (const [property, value] of Object.entries(layout))
      add("consistency", "overlay layout", dialog, value, false, `${dialog.classList.contains("side-panel") ? "panel" : "dialog"}/${property}`);
  }
  const compact = new Map<string, Sample>();
  for (const sample of samples) {
    const key = ["colour tokens", "spacing tokens"].includes(sample.rule) ? JSON.stringify([sample.rule, sample.group, sample.value]) : sample.selector + sample.rule + sample.group;
    const previous = compact.get(key);
    if (previous) { previous.selector += `, ${sample.selector}`; previous.count = (previous.count || 1) + 1; } else compact.set(key, sample);
  }
  return { controls, signature, samples: [...compact.values()], dialogs: dialogs.map(selector), tokenCount: rootTokens.size };
}

export function inspectFocus() {
  const element = document.activeElement as HTMLElement;
  const path = (item: Element): string => item.id && !/^(bits-|svelte-|radix-|:)/.test(item.id) ? `#${CSS.escape(item.id)}` : item === document.body ? "body" :
    `${path(item.parentElement!)} > ${item.tagName.toLowerCase()}:nth-of-type(${[...item.parentElement!.children].filter(other => other.tagName === item.tagName).indexOf(item) + 1})`;
  const snapshot = () => { const style = getComputedStyle(element); return { outline: `${style.outlineStyle}/${style.outlineWidth}/${style.outlineColor}`, boxShadow: style.boxShadow }; };
  const original = element.style.cssText;
  element.style.setProperty("transition", "none", "important");
  const focused = snapshot(), focusVisible = element.matches(":focus-visible"), box = element.getBoundingClientRect();
  element.blur(); const blurred = snapshot(); element.focus({ preventScroll: true });
  element.style.cssText = original;
  const indicator = focusVisible && (focused.outline !== blurred.outline && !focused.outline.startsWith("none/") ||
    focused.boxShadow !== blurred.boxShadow && focused.boxShadow !== "none");
  return { selector: path(element), focused, blurred, focusVisible, indicator,
    hidden: !box.width || !box.height || getComputedStyle(element).visibility !== "visible" || !!element.closest('[aria-hidden=true],[inert]') };
}
