export function sidePanelLayout(viewportWidth: number, contentWidth: number): { width: number; docked: boolean } {
  const width = viewportWidth < 900 ? viewportWidth : viewportWidth < 1440 ? Math.min(420, viewportWidth * .36) : 480;
  return { width, docked: viewportWidth >= 900 && contentWidth - width >= 520 };
}
