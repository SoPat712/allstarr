const ADMIN_BASE_PATH = /^\/(?:[A-Za-z0-9_-]+)(?:\/(?:[A-Za-z0-9_-]+))*$/;

function readAdminBasePath(): string {
  const dom = (globalThis as typeof globalThis & { document?: Document }).document;
  if (!dom) return "";

  let configured = "";
  try {
    configured = dom
      .querySelector('meta[name="allstarr-base-path"]')
      ?.getAttribute("content")
      ?.trim() ?? "";
  } catch {
    return "";
  }
  if (!configured || configured === "/") return "";

  const normalized = configured.endsWith("/") ? configured.slice(0, -1) : configured;
  return ADMIN_BASE_PATH.test(normalized) ? normalized : "";
}

/**
 * Prefixes a same-origin root-relative admin URL with the configured mount
 * path. Hash routes, relative URLs, and external URLs are intentionally left
 * untouched because they have different URL ownership semantics.
 */
export function adminUrl(path: string): string {
  if (!path || !path.startsWith("/") || path.startsWith("//") || path.startsWith("/\\"))
    return path;

  const basePath = readAdminBasePath();
  if (!basePath) return path;

  if (path === basePath ||
      path.startsWith(`${basePath}/`) ||
      path.startsWith(`${basePath}?`) ||
      path.startsWith(`${basePath}#`))
    return path;

  return path === "/" ? `${basePath}/` : `${basePath}${path}`;
}
