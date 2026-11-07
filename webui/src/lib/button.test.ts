import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import { Button, buttonVariants } from "./components/ui/button";

describe("text buttons", () => {
  it("gives destructive text actions a full-strength error colour and state layer", () => {
    const classes = buttonVariants({ variant: "destructive-text" });
    expect(classes).toContain("text-[var(--color-danger)]");
    expect(classes).toContain("bg-transparent");
    expect(classes.split(" ")).not.toContain("opacity-50");
  });
  it("renders a transparent control with signal text and ghost state layers", () => {
    const html = render(Button, { props: { variant: "text" } }).body;
    expect(html).toContain("text-[var(--color-signal-text)]");
    expect(html).toContain("border-transparent");
    for (const state of ["hover:bg-muted", "aria-expanded:bg-muted", "dark:hover:bg-muted/50"])
      expect(html).toContain(state);
    expect(html).not.toContain("text-foreground");
  });

  it("colours ghost and text icons like their labels", () => {
    for (const variant of ["ghost", "text"] as const) {
      for (const size of ["icon", "icon-sm", "icon-xs", "icon-lg"] as const) {
        const classes = buttonVariants({ variant, size });
        expect(classes).toContain("text-[var(--color-signal-text)]");
        expect(classes).not.toContain("text-foreground");
        for (const state of ["hover:bg-muted", "aria-expanded:bg-muted", "dark:hover:bg-muted/50"])
          expect(classes).toContain(state);
      }
    }
  });
});
