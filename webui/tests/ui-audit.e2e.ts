import { test, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { mkdir, readFile } from "node:fs/promises";
import { mockApi, responses } from "./parity-support";
import { touchTarget, expectContainedMobileGeometry } from "./mobile-geometry";
import { panelFixture, settingsFixture, homeFixture } from "./shared-design-fixtures";
import { inspectDom, inspectFocus } from "./ui-audit-dom";
import { writeReport, type Row } from "./ui-audit-report";

test.use({ browserName: "chromium", trace: "off" });

type Visit = { url: string; steps: string[]; id: string; origin?: string };
const directory = "test-results/ui-audit";
const destructive = /^(save|delete|remove|sign out|disconnect|play(?: |$)|pause|generate|regenerate|sync|refresh|run|start|stop|enable|disable|grant|revoke|send|submit|apply|clear|reset|keep |add to (?:your )?apps)/i;
const pageName = (url: string) => {
  const [path, query] = url.split("?"), params = new URLSearchParams(query), tabs = new URLSearchParams();
  for (const key of ["section", "view"]) if (params.has(key)) tabs.set(key, params.get(key)!);
  return path + (tabs.size ? `?${tabs}` : "");
};

test("measure access and consistency", async ({ browser }, testInfo) => {
  test.skip(!process.env.UI_AUDIT, "Run with UI_AUDIT=1");
  test.setTimeout(600_000);
  const started = Date.now(), rows: Row[] = [], limits: string[] = [], omitted: string[] = [];
  const captures = new Map<string, string>();
  let states = 0;
  await mkdir(directory, { recursive: true });
  const routeSource = await readFile("src/routes/[...path]/+page.svelte", "utf8");
  const seeds = [...new Set([...routeSource.matchAll(/href:\s*"(#\/[^\"]*)"/g)].map(match => match[1]))];
  const configurations = [1280, 390].flatMap(width => ["light", "dark"].flatMap(theme => [true, false].map(admin => ({ width, theme, admin, fixture: "base" }))));
  // Run the optional, most expensive theme last so it is the first coverage cut.
  configurations.sort((a, b) => Number(a.width === 390 && a.theme === "dark") - Number(b.width === 390 && b.theme === "dark"));
  configurations.splice(6, 0, ...["panels", "settings", "home"].flatMap(fixture => [true, false].map(admin => ({ width: 1280, theme: "light", admin, fixture }))));
  const pendingOverlays = new Map<string, Visit[]>(), pageDistances = new Map<string, Map<string, number>>();
  let nextConfiguration = 0, exploringOverlays = false;
  async function measureConfigurations() {
    while (nextConfiguration < configurations.length) {
      const config = configurations[nextConfiguration++];
      const state = `${config.admin ? "administrator" : "listener"}/${config.width}/${config.theme}/${config.fixture}`;
      if (config.width === 390 && config.theme === "dark" && Date.now() - started > 420_000) { omitted.push(state); continue; }
      if (Date.now() - started > 550_000) { omitted.push(state); continue; }
      const context = await browser.newContext({ baseURL: testInfo.project.use.baseURL, viewport: { width: config.width, height: config.width === 390 ? 844 : 800 },
        colorScheme: config.theme as "light" | "dark", reducedMotion: "reduce", serviceWorkers: "block", locale: "en-US", timezoneId: "America/New_York" });
      const page = await context.newPage();
      await page.clock.setFixedTime(new Date("2026-11-12T17:00:00Z"));
      page.setDefaultTimeout(1200);
      page.on("dialog", dialog => void dialog.dismiss());
      await page.addInitScript(theme => localStorage.setItem("allstarr.theme", theme), config.theme);
      await mockApi(page);
      if (config.fixture === "panels") await panelFixture(page);
      if (config.fixture === "settings") await settingsFixture(page);
      if (config.fixture === "home") await homeFixture(page, config.admin);
      // The shared mock has no listener option; override only its session as other specs do.
      await page.route("**/api/admin/auth/me", route => route.fulfill({ json: {
        ...(responses["/api/admin/auth/me"] as object), features: { intelligence: true },
        user: { id: "listener", allstarrUserId: "listener", name: "Listener", isAdministrator: config.admin },
      } }));
      let currentPage = "#/", currentControl = "request", writes = 0;
      // Unmatched requests, including requests during navigation, must never reach a real backend.
      await context.route("**/*", route => {
        const url = new URL(route.request().url());
        if (!["127.0.0.1", "localhost"].includes(url.hostname)) return route.abort();
        if (url.pathname.startsWith("/api")) return route.fulfill({ status: 404, json: { error: "Audit fixture unavailable" } });
        return route.fallback();
      });
      await page.route("**/*", async route => {
        const request = route.request(), url = new URL(request.url());
        if (url.pathname.startsWith("/api") && request.method() !== "GET") {
          writes++;
          rows.push({ measurement: "safety", rule: "blocked write", page: pageName(currentPage), state, selector: currentControl, value: { method: request.method(), path: url.pathname }, flagged: false });
          return route.abort();
        }
        if (!["127.0.0.1", "localhost"].includes(url.hostname)) {
          rows.push({ measurement: "safety", rule: "blocked external request", page: pageName(currentPage), state, selector: "request", value: url.origin + url.pathname, flagged: false });
          return route.abort();
        }
        if (url.pathname.startsWith("/api/") && !url.pathname.startsWith("/api/admin/")) return route.fulfill({ json: {} });
        return route.fallback();
      });
      const queue: Visit[] = [], queued = new Set<string>(), visited = new Map<string, Visit>(), aliases = new Map<string, string>();
      const expected = new Map<string, { signature: string; url: string; steps: string[] }>();
      const edges: [string, string][] = [], reachRows = new Map<string, Row>();
      const enqueue = (url: string, steps: string[] = []) => {
        const id = steps.length ? `${url}::${steps.join("::")}` : `url:${url}`;
        if (!queued.has(id)) { queued.add(id); queue.push({ url, steps, id }); }
        return id;
      };
      const initial = config.fixture === "home" ? ["#/"] : config.fixture === "settings" ? ["#/settings"] :
        config.fixture === "panels" ? seeds.filter(url => /library|sources|for-you|activity/.test(url)) : seeds;
      if (exploringOverlays) {
        for (const visit of pendingOverlays.get(state) || []) { queued.add(visit.id); queue.push(visit); }
      } else {
        initial.forEach(url => enqueue(url));
        pendingOverlays.set(state, []);
      }
      async function settle() {
        await page.locator(".workspace").waitFor({ timeout: 8000 });
        await page.locator('.skeleton-panel,[aria-busy=true],.rows-skeleton').first().waitFor({ state: "hidden", timeout: 2000 }).catch(() => {});
        await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
      }
      async function restore(visit: Visit) {
        currentPage = visit.url;
        // Start a fresh document; hash navigation can race the application's own router.
        await page.goto("about:blank");
        await page.goto(visit.url, { waitUntil: "domcontentloaded", timeout: 10_000 });
        await page.waitForLoadState("networkidle", { timeout: 750 }).catch(() => {});
        await page.addStyleTag({ content: "*,*::before,*::after{transition-duration:0s!important;animation-duration:0s!important;animation-delay:0s!important}" });
        await settle();
        for (const selector of visit.steps) { await page.locator(selector).click({ timeout: 1200 }); await settle(); }
        await page.mouse.move(1, 1);
        currentPage = new URL(page.url()).hash;
      }
      for (let index = 0; index < queue.length; index++) {
        if (Date.now() - started > 550_000 || config.width === 390 && config.theme === "dark" && Date.now() - started > 480_000) {
          omitted.push(`${state}: ${queue.length - index} queued states`); break;
        }
        const visit = queue[index];
        try {
        await restore(visit);
        let dom = await page.evaluate(inspectDom, false);
        aliases.set(visit.id, dom.signature);
        const clicked = expected.get(visit.id);
        if (clicked && clicked.signature !== dom.signature) {
          const fallback = enqueue(clicked.url, clicked.steps);
          aliases.set(visit.id, clicked.signature);
          aliases.set(fallback, clicked.signature);
        }
        if (visited.has(dom.signature) || exploringOverlays && dom.signature === visit.origin) continue;
        visited.set(dom.signature, visit); const visitNumber = ++states;
        const signature = dom.signature, local: Row[] = [];
        const add = (measurement: string, rule: string, selector: string, value: unknown, flagged = false) => {
          const row = { measurement, rule, selector, value, flagged, page: pageName(currentPage), state: `${state}/${signature}` };
          local.push(row); return row;
        };
        const denied = await page.getByText("This workspace is for administrators.", { exact: true }).isVisible();
        const reach = add("reachability", "minimum clicks", dom.dialogs.at(-1) || ".workspace", { clicks: null, directUrl: !visit.steps.length, accessDenied: denied });
        reachRows.set(signature, reach);
        const overlay = dom.dialogs.at(-1), opener = visit.steps.at(-1);
        if (overlay) add("keyboard", "focus enters overlay", overlay, await page.locator(overlay).evaluate(element => element.contains(document.activeElement)),
          !await page.locator(overlay).evaluate(element => element.contains(document.activeElement)));
        try {
          const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
          add("accessibility", "rules evaluated", "body", { passes: axe.passes.length, incomplete: axe.incomplete.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) })) });
          for (const violation of axe.violations) for (const node of violation.nodes)
            add("accessibility", violation.id, node.target.join(" "), { impact: violation.impact, selectors: node.target, summary: node.failureSummary }, true);
        } catch (error) { add("accessibility", "scan unavailable", "body", String(error), true); }
        dom.samples.forEach(sample => local.push({ ...sample, page: pageName(currentPage), state: `${state}/${signature}` }));
        if (!dom.tokenCount) limits.push(`${state} ${visit.url}: no readable :root tokens`);
        const seen = new Set<string>();
        if (!overlay) await page.evaluate(() => { const previous = document.body.getAttribute("tabindex"); document.body.tabIndex = -1; document.body.focus();
          if (previous === null) document.body.removeAttribute("tabindex"); else document.body.setAttribute("tabindex", previous); });
        let escaped = false;
        for (let stop = 0; stop < 150; stop++) {
          await page.keyboard.press("Tab");
          const focus = await page.evaluate(inspectFocus);
          if (seen.has(focus.selector)) break;
          seen.add(focus.selector);
          const accessible = await page.locator(focus.selector).ariaSnapshot().catch(() => "");
          const entry = accessible.split("\n")[0]?.match(/^- ([\w-]+)(?: "([^"]*)")?/);
          add("keyboard", "focus stop", focus.selector, { stop, role: entry?.[1], accessibleName: entry?.[2] || "", accessible, ...focus }, focus.hidden || !focus.indicator);
          if (overlay && !await page.locator(overlay).evaluate(element => element.contains(document.activeElement))) escaped = true;
          if (stop === 149) limits.push(`${state} ${visit.id}: keyboard capped at 150 stops`);
        }
        const scopedControls = dom.controls.filter(control => !overlay || control.overlay === overlay);
        for (const control of scopedControls) {
          if (["generic", "dialog", "alertdialog", "region"].includes(control.role) && control.tabIndex < 0) continue;
          if (!seen.has(control.selector)) add("keyboard", "unreached interactive element", control.selector,
            { role: control.role, name: control.name, tabIndex: control.tabIndex }, true);
          if (config.width === 390) {
            try { const target = await touchTarget(page.locator(control.selector));
              add("mobile", "44px touch target", control.selector, target, target.width < 44 || target.height < 44);
            } catch (error) { add("mobile", "target unavailable", control.selector, String(error), true); }
          }
        }
        if (overlay) add("keyboard", "overlay Tab containment", overlay, !escaped, escaped);
        if (config.width === 390) {
          // Record the shared helper's evaluation before its assertions, then let all four checks complete.
          const recorder = { evaluate: async (callback: () => Promise<unknown>) => {
            const result = await page.evaluate(callback) as Record<string, string[]>;
            for (const [rule, failures] of Object.entries(result)) add("mobile", rule, "body", failures, failures.length > 0);
            return Object.fromEntries(Object.keys(result).map(key => [key, []]));
          } } as unknown as Page;
          await expectContainedMobileGeometry(recorder);
        }
        // Capture candidates now: cross-page outliers can only be classified after all states are measured.
        const evidence = `${visitNumber}.png`;
        const selectors = [...new Set(["a[href]", ".workspace", ...local.filter(row => row.flagged || ["button styles", "heading styles", "page header", "overlay layout", "labels"].includes(row.rule)).map(row => row.selector)])];
        await page.evaluate(selectors => {
          (window as unknown as { auditStyles: [HTMLElement, string][] }).auditStyles = [];
          (window as unknown as { auditMarkers: HTMLElement[] }).auditMarkers = [];
          const marked = new Set<HTMLElement>();
          for (const selector of selectors) { let elements: NodeListOf<HTMLElement>; try { elements = document.querySelectorAll(selector); } catch { continue; }
            for (const element of elements) { if (marked.has(element)) continue; marked.add(element);
              (window as unknown as { auditStyles: [HTMLElement, string][] }).auditStyles.push([element, element.style.cssText]);
              element.style.setProperty("outline", "2px solid red", "important"); element.style.setProperty("outline-offset", "1px", "important");
              const box = element.getBoundingClientRect();
              if (!box.width || !box.height) { const marker = document.createElement("span");
                marker.style.cssText = `position:absolute;left:${Math.max(0, box.x + scrollX)}px;top:${Math.max(0, box.y + scrollY)}px;width:12px;height:12px;outline:2px solid red;z-index:2147483647;pointer-events:none`;
                document.body.append(marker); (window as unknown as { auditMarkers: HTMLElement[] }).auditMarkers.push(marker); }
            }
          }
        }, selectors);
        await page.screenshot({ path: `${directory}/${evidence}`, fullPage: true, animations: "disabled" });
        captures.set(`${state}/${signature}`, evidence);
        await page.evaluate(() => { for (const [element, style] of (window as unknown as { auditStyles: [HTMLElement, string][] }).auditStyles) element.style.cssText = style;
          for (const marker of (window as unknown as { auditMarkers: HTMLElement[] }).auditMarkers) marker.remove(); });
        local.forEach(row => { row.evidence = evidence; });
        if (overlay) {
          await page.keyboard.press("Escape");
          await page.locator(overlay).waitFor({ state: "hidden", timeout: 600 }).catch(() => {});
          const closed = !await page.locator(overlay).isVisible();
          add("keyboard", "Escape closes overlay", overlay, closed, !closed);
          if (opener) { const returned = await page.locator(opener).evaluate(element => element === document.activeElement).catch(() => false);
            add("keyboard", "focus returns to opener", opener, returned, !returned); }
          local.forEach(row => { row.evidence = evidence; });
          await restore(visit); dom = await page.evaluate(inspectDom, false);
        }
        rows.push(...local);
        const candidates = exploringOverlays ? [] : dom.controls.filter(control => control.href?.startsWith("#/") ||
          ["button", "tab"].includes(control.role) && !destructive.test(control.name) && !/^(close|cancel|back|collapse|appearance|system|light|dark)/i.test(control.name));
        let dirty = false;
        const patterns = new Set<string>();
        for (const control of candidates) {
          if (Date.now() - started > 550_000) break;
          if (control.href?.startsWith("#/")) {
            const allowed = config.fixture === "base" || config.fixture === "settings" && control.href.startsWith("#/settings") ||
              config.fixture === "panels" && /library|sources|for-you|activity/.test(control.href) || control.href === "#/";
            if (!allowed) continue;
            edges.push([signature, enqueue(control.href)]);
            continue;
          }
          if (config.fixture === "home" && control.role !== "tab" && control.popup !== "dialog") continue;
          const pattern = control.role === "tab" ? control.selector : control.selector.replace(/:nth-of-type\(\d+\)/g, "");
          if (patterns.has(pattern)) continue;
          patterns.add(pattern);
          if (control.role !== "tab") {
            const steps = [...visit.steps, control.selector];
            pendingOverlays.get(state)!.push({ url: visit.url, steps, id: `${visit.url}::${steps.join("::")}`, origin: signature });
            continue;
          }
          try {
            if (dirty) await restore(visit);
            dirty = false;
            const before = writes;
            currentControl = control.selector;
            await page.locator(control.selector).click({ timeout: 600 }); await settle();
            currentControl = "request";
            if (writes !== before) { dirty = true; continue; }
            const next = await page.evaluate(inspectDom, true);
            if (next.signature === signature) continue;
            dirty = true;
            const href = new URL(page.url()).hash;
            const nextId = href !== visit.url ? enqueue(href) : enqueue(visit.url, [...visit.steps, control.selector]);
            if (href !== visit.url && !expected.has(nextId)) expected.set(nextId, { signature: next.signature, url: visit.url, steps: [...visit.steps, control.selector] });
            aliases.set(nextId, next.signature); edges.push([signature, nextId]);
            const opened = next.dialogs.at(-1);
            if (opened && !dom.dialogs.includes(opened)) {
              const lifecycle = (rule: string, selector: string, passed: boolean) => rows.push({ measurement: "keyboard", rule,
                selector, value: passed, flagged: !passed, page: pageName(href), state: `${state}/${next.signature}` });
              lifecycle("focus enters on click", opened, await page.locator(opened).evaluate(element => element.contains(document.activeElement)));
              await page.keyboard.press("Escape");
              await page.locator(opened).waitFor({ state: "hidden", timeout: 600 }).catch(() => {});
              lifecycle("Escape closes on click", opened, !await page.locator(opened).isVisible());
              lifecycle("focus returns after click", control.selector, await page.locator(control.selector).evaluate(element => element === document.activeElement).catch(() => false));
            }
          } catch (error) { dirty = true; currentControl = "request"; limits.push(`${state} ${visit.url} ${control.name}: click unavailable: ${String(error).slice(0, 100)}`); }
        }
        } catch (error) {
          const detail = String(error).slice(0, 200);
          limits.push(`${state} ${visit.id}: state scan interrupted: ${detail}`);
          rows.push({ measurement: "coverage", rule: "state scan interrupted", page: pageName(visit.url), state, selector: ".workspace", value: detail, flagged: true });
        }
      }
      const home = aliases.get("url:#/"), distances = new Map<string, number>();
      if (home) { distances.set(home, 0); const pending = [home];
        for (const source of pending) for (const [from, to] of edges) {
          const target = aliases.get(to); if (from !== source || !target || distances.has(target)) continue;
          distances.set(target, distances.get(source)! + 1); pending.push(target);
        }
      }
      for (const [signature, row] of reachRows) {
        const value = row.value as { clicks: number | null; directUrl: boolean; accessDenied: boolean };
        const origin = visited.get(signature)?.origin;
        const before = origin ? pageDistances.get(state)?.get(origin) : undefined;
        value.clicks = exploringOverlays ? before === undefined ? null : before + 1 : distances.get(signature) ?? null;
        row.flagged = value.clicks !== null && value.clicks > 3;
      }
      if (!exploringOverlays) pageDistances.set(state, distances);
      await context.close();
    }
  }
  try {
    await Promise.all([measureConfigurations(), measureConfigurations()]);
    exploringOverlays = true; nextConfiguration = 0;
    await Promise.all([measureConfigurations(), measureConfigurations()]);
  } finally {
    for (const row of rows) row.evidence ||= captures.get(row.state);
    await writeReport(directory, rows, { generatedAt: new Date().toISOString(), fixtureClock: "2026-11-12T17:00:00Z", browser: browser.version(), durationSeconds: Math.round((Date.now() - started) / 1000), states, omitted, limits,
      viewports: ["1280x800", "390x844"], themes: ["light", "dark"], seeds,
      limitations: ["Pages and tabs are measured before representative one-click dialogs; the time budget may omit later dialog states.",
        "Shared fixtures stub fonts and artwork; fixture error states can differ from a configured live server.",
        "Extra populated fixtures run at desktop light only; base fixtures cover the full matrix.",
        "Focus indicators use computed outline/boxShadow differences, not visual contrast or occlusion.",
        "Sentence case, action synonyms and date formats need human review for proper nouns and different action meanings.",
        "Resolved colour equality checks token membership, not which declaration supplied the colour; pseudo-elements and gradients are not enumerated.",
        "Repeated controls with the same DOM structure are sampled once per page; every tab and distinct link destination is retained.",
        "Reachability covers rendered links, tabs and safe buttons; forms, destructive controls, native pickers and signed-in external services are excluded.",
        "Roving tabs may be reachable with arrow keys while absent from the Tab sequence."] });
  }
});
