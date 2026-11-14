import { writeFile } from "node:fs/promises";

export type Row = { measurement: string; rule: string; page: string; state: string; selector: string; value: unknown;
  flagged: boolean; group?: string; count?: number; screenshot?: string; evidence?: string };
export function classify(rows: Row[]) {
  // The browser's own focus boundary and non-focusable containers are not missing controls.
  for (const row of rows) {
    const control = row.value as { role?: string; tabIndex?: number } | null;
    if (row.rule === "focus stop" && row.selector === "body" || row.rule === "unreached interactive element" &&
      control?.tabIndex === -1 && ["tablist", "toolbar", "group", "radiogroup", "listbox"].includes(control.role || "")) {
      row.flagged = false; delete row.screenshot;
    }
  }
  const actions = [["Add to apps", "Add to your apps"], ["Remove", "Delete"], ["Save", "Save changes"], ["Close", "Done"]];
  for (const variants of actions) {
    const matches = rows.filter(row => row.rule === "labels" && variants.includes(String(row.value)));
    if (new Set(matches.map(row => row.value)).size > 1 && new Set(matches.map(row => row.page)).size > 1)
      matches.forEach(row => { row.flagged = true; row.group = `action wording: ${variants.join(" / ")}`; });
  }
  const groups = new Map<string, Row[]>();
  for (const row of rows.filter(row => ["button styles", "heading styles", "page header", "overlay layout", "colour tokens", "spacing tokens", "labels", "date/time formats"].includes(row.rule))) {
    const key = `${row.rule}/${row.group || ""}/${row.state.split("/").slice(1, 3).join("/")}`;
    groups.set(key, [...(groups.get(key) || []), row]);
  }
  const distributions: { rule: string; group: string; value: unknown; count: number; pages: string[]; flagged: boolean }[] = [];
  for (const [group, members] of groups) {
    const values = new Map<string, Row[]>();
    for (const row of members) {
      const value = row.rule === "date/time formats" ? row.group! : JSON.stringify(row.value);
      values.set(value, [...(values.get(value) || []), row]);
    }
    const largest = Math.max(...[...values.values()].map(items => new Set(items.map(row => row.page)).size));
    for (const items of values.values()) {
      const pages = [...new Set(items.map(row => row.page))];
      const count = [...new Map(items.map(row => [`${row.page}/${row.selector}`, row.count || 1])).values()].reduce((sum, value) => sum + value, 0);
      const flagged = items[0].rule === "button styles" ? count < 3 || pages.length === 1 :
        ["page header", "overlay layout"].includes(items[0].rule) ? pages.length < largest : false;
      if (flagged) items.forEach(row => { row.flagged = true; });
      distributions.push({ rule: items[0].rule, group, value: items[0].value, count, pages, flagged: flagged || items.some(row => row.flagged) });
    }
  }
  rows.forEach(row => { if (row.flagged && row.evidence) row.screenshot = row.evidence; delete row.evidence; });
  return distributions;
}
export async function writeReport(directory: string, rows: Row[], metadata: Record<string, unknown>) {
  const distributions = classify(rows);
  metadata = { ...metadata, measuredStates: rows.filter(row => row.measurement === "reachability").length,
    coverageComplete: !(metadata.omitted as unknown[] | undefined)?.length && !(metadata.limits as unknown[] | undefined)?.length };
  const categories = [...new Set(rows.map(row => row.measurement))];
  const summary = categories.map(measurement => ({ measurement, rows: rows.filter(row => row.measurement === measurement).length,
    flagged: rows.filter(row => row.measurement === measurement && row.flagged).length }));
  const issues = new Map<string, Row[]>();
  for (const row of rows.filter(row => row.flagged)) {
    const detail = ["colour tokens", "spacing tokens", "button styles", "page header", "overlay layout"].includes(row.rule) ? `: ${JSON.stringify(row.value)}` : "";
    const key = `${row.measurement}: ${row.rule}${row.group ? ` (${row.group})` : ""}${detail}`;
    issues.set(key, [...(issues.get(key) || []), row]);
  }
  const top = [...issues].map(([issue, items]) => ({ issue, pages: [...new Set(items.map(row => row.page))], count: items.reduce((sum, row) => sum + (row.count || 1), 0),
    screenshot: items.find(row => row.screenshot)?.screenshot })).sort((a, b) => b.pages.length - a.pages.length || b.count - a.count).slice(0, 20);
  const cell = (value: unknown) => (typeof value === "string" ? value : JSON.stringify(value)).replace(/\|/g, "\\|").replace(/\n/g, " ");
  const table = (headings: string[], data: unknown[][]) => [headings.join(" | "), headings.map(() => "---").join(" | "), ...data.map(cells => cells.map(cell).join(" | "))].join("\n");
  const lines = ["# WebUI access and consistency audit", "", `Duration: ${metadata.durationSeconds}s. Measured states: ${metadata.measuredStates} of ${metadata.states} attempted. Coverage: ${metadata.coverageComplete ? "complete" : "partial; see omissions and limits below"}.`, "",
    table(["Measurement", "Rows", "Flagged"], summary.map(item => [item.measurement, item.rows, item.flagged])), "", "## Top 20 issues", "",
    table(["Issue", "Pages", "Occurrences", "Evidence"], top.map(item => [item.issue, item.pages.length, item.count, item.screenshot ? `[image](${item.screenshot})` : "Unavailable"])), "",
    "Counts describe measured occurrences, including repeated themes and roles. Rare styles and wording are review candidates, not confirmed defects.", "",
    "Screenshots outline flagged elements and consistency candidates in red; the selector identifies the row's element. Hidden stops use a red marker at their box origin.", "",
    "## Coverage and limits", "", "```json", JSON.stringify(metadata, null, 2), "```", ""];
  for (const category of categories) {
    lines.push(`## ${category}`, "", table(["Page", "State", "Rule", "Selector", "Value", "Flag", "Screenshot"],
      rows.filter(row => row.measurement === category).map(row => [row.page, row.state, row.rule, row.selector, row.value, row.flagged ? "Review" : "", row.screenshot ? `[image](${row.screenshot})` : ""])), "");
  }
  lines.push("## Style and wording distributions", "", table(["Rule / group", "Value", "Elements", "Pages", "Flag"], distributions.map(item =>
    [item.group, item.value, item.count, item.pages.join(", "), item.flagged ? "Review" : ""])));
  await writeFile(`${directory}/report.json`, JSON.stringify({ schemaVersion: 1, ...metadata, summary, top, distributions, rows }, null, 2));
  await writeFile(`${directory}/report.md`, lines.join("\n"));
}
