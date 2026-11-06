export function sidePanelLayout(viewportWidth: number, contentWidth: number, options: { width?: number; minimumContentWidth?: number } = {}): { width: number; docked: boolean } {
  const preferredWidth = viewportWidth < 1440 ? Math.min(420, viewportWidth * .36) : 480;
  const width = viewportWidth < 900 ? viewportWidth : options.width === undefined ? preferredWidth : Math.min(options.width, viewportWidth * .4);
  return { width, docked: viewportWidth >= 900 && contentWidth - width >= (options.minimumContentWidth ?? 520) };
}
