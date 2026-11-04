import { expect, type Locator } from "@playwright/test";

export async function expectPageSurface(element: Locator) {
  await expect(element).toHaveCSS("background-color", "rgba(0, 0, 0, 0)");
  await expect(element).toHaveCSS("border-radius", "0px");
}

export async function expectSubtleSelection(row: Locator, title: Locator) {
  await row.page().mouse.move(0, 0);
  const expected = await row.evaluate(element => {
    const sample = document.createElement("span");
    sample.style.background = "color-mix(in srgb, var(--color-signal) 9%, var(--color-panel))";
    sample.style.color = "var(--color-ink)";
    element.append(sample);
    const style = getComputedStyle(sample);
    const colors = { background: style.backgroundColor, ink: style.color };
    sample.remove();
    return colors;
  });
  await expect(row).toHaveCSS("background-color", expected.background);
  await expect(title).toHaveCSS("color", expected.ink);
}

export async function expectToolbarLayout(toolbar: Locator, filters: Locator) {
  const controls = toolbar.locator("input, .select-trigger");
  const boxes = await controls.evaluateAll(elements => elements.map(element => {
    const box = element.getBoundingClientRect();
    const label = element.querySelector("[data-select-value]");
    return { y: box.y + box.height / 2, clipped: label ? label.scrollWidth > label.clientWidth : false };
  }));
  expect(boxes).toHaveLength(3);
  expect(Math.max(...boxes.map(box => box.y)) - Math.min(...boxes.map(box => box.y))).toBeLessThanOrEqual(1);
  expect(boxes.some(box => box.clipped)).toBe(false);
  const filterBox = await filters.boundingBox(), toolbarBox = await toolbar.boundingBox();
  expect(filterBox!.y + filterBox!.height).toBeLessThanOrEqual(toolbarBox!.y);
}

export async function expectSingleRowFooter(panel: Locator) {
  const boxes = await panel.locator(".panel-footer [data-slot='button']").evaluateAll(elements => elements.map(element => {
    const box = element.getBoundingClientRect(), footer = element.closest("footer")!.getBoundingClientRect();
    return { y: box.y + box.height / 2, contained: box.x >= footer.x && box.right <= footer.right };
  }));
  expect(boxes.length).toBeGreaterThan(1);
  expect(Math.max(...boxes.map(box => box.y)) - Math.min(...boxes.map(box => box.y))).toBeLessThanOrEqual(1);
  expect(boxes.every(box => box.contained)).toBe(true);
}
