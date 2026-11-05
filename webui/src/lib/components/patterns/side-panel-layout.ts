export function sidePanelLayout(viewportWidth: number, contentWidth: number): { width: number; docked: boolean } {
  const width = viewportWidth < 900 ? viewportWidth : Math.min(480, viewportWidth * .4);
  return { width, docked: viewportWidth >= 900 && contentWidth - width >= 640 };
}
