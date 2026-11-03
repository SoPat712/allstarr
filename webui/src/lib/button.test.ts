import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import { Button, buttonVariants } from "./components/ui/button";

describe("text buttons", () => {
  it("renders a transparent control with signal text and ghost state layers", () => {
    const html = render(Button, { props: { variant: "text" } }).body;
    expect(html).toContain("text-[var(--color-signal-text)]");
    expect(html).toContain("border-transparent");
    for (const state of ["hover:bg-muted", "aria-expanded:bg-muted", "dark:hover:bg-muted/50"])
      expect(html).toContain(state);
    expect(html).not.toContain("text-foreground");
  });

  it("keeps ghost icon controls free of the signal text override", () => {
    for (const size of ["icon", "icon-sm", "icon-xs", "icon-lg"] as const) {
      const classes = buttonVariants({ variant: "ghost", size });
      expect(classes).not.toContain("text-[var(--color-signal-text)]");
      expect(classes).toContain("hover:text-foreground");
      expect(classes).toContain("aria-expanded:text-foreground");
    }
  });
});
