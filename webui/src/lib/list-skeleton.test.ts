import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import ListSkeleton from "./components/ListSkeleton.svelte";

describe("list loading placeholders", () => {
  it("announces loading once and hides six decorative rows by default", () => {
    const html = render(ListSkeleton).body;
    expect(html).toContain('aria-busy="true"');
    expect(html).toContain('aria-label="Loading"');
    expect(html).toContain('role="status"');
    expect(html.match(/class="list-skeleton-row\b/g)).toHaveLength(6);
    expect(html.match(/class="list-skeleton-placeholder list-skeleton-avatar/g)).toHaveLength(6);
    expect(html.match(/class="list-skeleton-placeholder list-skeleton-bar/g)).toHaveLength(12);
  });

  it("supports a smaller text-only list", () => {
    const html = render(ListSkeleton, { props: { rows: 3, avatar: false } }).body;
    expect(html.match(/class="list-skeleton-row\b/g)).toHaveLength(3);
    expect(html).not.toContain("list-skeleton-avatar");
    expect(html.match(/class="list-skeleton-placeholder list-skeleton-bar/g)).toHaveLength(6);
  });
});
