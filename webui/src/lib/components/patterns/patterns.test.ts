import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import PageStatus from "./PageStatus.svelte";
import FilterSegments from "./FilterSegments.svelte";
import ItemRow from "./ItemRow.svelte";
import GroupedRow from "./GroupedRow.svelte";
import RowsSkeleton from "./RowsSkeleton.svelte";
import ErrorState from "./ErrorState.svelte";
import SplitButton from "./SplitButton.svelte";

describe("shared pattern accessibility", () => {
  it("shows plain status text with a strong total and separate legend markers", () => {
    const html = render(PageStatus, { props: { items: [{ label: "6 songs" }, { label: "2 need a look", tone: "warn" }], segments: [
      { label: "Matched", value: 4, tone: "good" }, { label: "Unsure", value: 2, tone: "warn" },
    ] } }).body;
    expect(html).toMatch(/<strong[^>]*>6 songs<\/strong>/);
    expect(html).not.toContain("status-dot");
    expect(html).toContain("legend-swatch");
    expect(html).toContain('data-tone="warn"');
  });
  it("keeps every count in the status legend, including zero segments", () => {
    const html = render(PageStatus, { props: { items: [{ label: "6 songs" }], segments: [
      { label: "Matched", value: 6, tone: "good" }, { label: "Waiting", value: 0, tone: "warn" },
    ] } }).body;
    expect(html).toContain('aria-label="Matched: 6, Waiting: 0"');
    expect(html).toContain('aria-label="Status breakdown"');
    expect(html).toContain("Waiting");
    expect(html).toMatch(/legend-count[^>]*>0</);
    expect(html).toContain('data-tone="neutral"');
  });
  it("offers zero counts as clickable radio options with one tab stop", () => {
    const html = render(FilterSegments, { props: { options: [{ id: "all", label: "All", count: 3 }, { id: "waiting", label: "Waiting", count: 0 }], value: "all" } }).body;
    expect(html.match(/role="radio"/g)).toHaveLength(2);
    expect(html.match(/tabindex="0"/g)).toHaveLength(1);
    expect(html).toContain('aria-checked="true"');
    expect(html).not.toContain("disabled");
  });
  it("provides a single named row opener and reflects selection", () => {
    const html = render(ItemRow, { props: { title: "Song", subtitle: "Artist", selected: true, attention: true, onclick: () => {} } }).body;
    expect(html.match(/<button/g)).toHaveLength(1);
    expect(html).toContain('aria-pressed="true"');
    expect(html).toContain('aria-labelledby=');
    expect(html).toContain("needs-review");
    expect(html).not.toMatch(/class="[^"]*\battention\b/);
    const link = render(ItemRow, { props: { title: "Song", href: "#/song" } }).body;
    expect(link).toContain('href="#/song"');
    expect(link).not.toContain("<button");
  });
  it("allows a descriptive opener name without repeating the visible title", () => {
    const html = render(ItemRow, { props: { title: "Song", actionLabel: "Open match for Song", onclick: () => {} } }).body;
    expect(html).toContain('aria-label="Open match for Song"');
    expect(html).not.toContain("aria-labelledby=");
    expect(html.match(/>Song</g)).toHaveLength(1);
  });
  it("exposes the group's expanded state and hides only collapsed members", () => {
    const collapsed = render(GroupedRow, { props: { count: 4, summary: "Similar songs" } }).body;
    const expanded = render(GroupedRow, { props: { count: 4, summary: "Similar songs", expanded: true } }).body;
    expect(collapsed).toContain('aria-expanded="false"');
    expect(collapsed).toMatch(/class="group-members[^>]* hidden/);
    expect(expanded).toContain('aria-expanded="true"');
    expect(expanded).not.toMatch(/class="group-members[^>]* hidden/);
  });
  it("bounds invalid skeleton counts and announces busy content once", () => {
    const html = render(RowsSkeleton, { props: { count: 2.8 } }).body;
    expect(html.match(/class="skeleton-row /g)).toHaveLength(2);
    expect(html).toContain('aria-busy="true"');
    expect(render(RowsSkeleton, { props: { count: -1 } }).body).not.toContain('class="skeleton-row ');
  });
  it("provides retry only when it can run and names the selected split action scope", () => {
    expect(render(ErrorState, { props: { title: "Matches could not load", onretry: async () => {} } }).body).toContain("Try again");
    expect(render(ErrorState, { props: { title: "Matches could not load" } }).body).not.toContain("Try again");
    const html = render(SplitButton, { props: { label: "Use version", value: "personal", options: [{ id: "personal", label: "For me" }] } }).body;
    expect(html).toContain("Use version: For me; choose scope");
  });
});
