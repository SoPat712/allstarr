import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import SegmentedNav from "./SegmentedNav.svelte";
import ThreeDetentSwitch from "./ThreeDetentSwitch.svelte";
import SourceUseSwitch from "./SourceUseSwitch.svelte";

describe("segmented controls", () => {
  it("keeps linked tabs and count pills in the shared outlined group", () => {
    const html = render(SegmentedNav, { props: { label: "Library", active: "songs", items: [
      { id: "songs", label: "Songs", href: "#/songs", count: 2 }, { id: "albums", label: "Albums", href: "#/albums", count: 0 },
    ] } }).body;
    expect(html).toMatch(/class="seg segmented-tabs/);
    expect(html).toContain('role="tablist"');
    expect(html.match(/tabindex="0"/g)).toHaveLength(1);
    expect(html).toContain('aria-selected="true"');
    expect(html).toContain('href="#/songs"');
    expect(html).toMatch(/segmented-tab-count[^>]*>0</);
  });

  it("preserves a named setting and one selected tab stop for four choices", () => {
    const html = render(ThreeDetentSwitch, { props: { label: "Source use", name: "source", value: "follow", options: [
      { value: "follow", label: "Follow" }, { value: "on", label: "On" }, { value: "manual", label: "Manual only" }, { value: "off", label: "Off" },
    ], onchange: () => {} } }).body;
    expect(html).toContain('class="seg use-switch"');
    expect(html).toContain('name="source" value="follow"');
    expect(html.match(/role="radio"/g)).toHaveLength(4);
    expect(html.match(/tabindex="0"/g)).toHaveLength(1);
    expect(html.match(/aria-checked="true"/g)).toHaveLength(1);
    expect(html).not.toContain("use-switch-thumb");
  });

  it("disables every source choice while keeping its selected value", () => {
    const html = render(SourceUseSwitch, { props: { label: "Use this source", value: "off", disabled: true, onchange: () => {} } }).body;
    expect(html).toContain('aria-disabled="true"');
    expect(html.match(/ disabled/g)).toHaveLength(3);
    expect(html).toMatch(/aria-checked="true"[^>]*>[^]*?<span>Off<\/span>/);
  });
});
