import type { ExtensionPackage, ExtensionStoreItem } from "$lib/api";

export function compareVersions(left: string, right: string) {
  const parts = (value: string) => value.replace(/^v/i, "").split(/[.+-]/)
    .map((part) => /^\d+$/.test(part) ? Number(part) : part.toLowerCase());
  const a = parts(left);
  const b = parts(right);
  for (let index = 0; index < Math.max(a.length, b.length); index++) {
    const av = a[index] ?? 0;
    const bv = b[index] ?? 0;
    if (av === bv) continue;
    if (typeof av === "number" && typeof bv === "number") return av - bv;
    if (typeof av === "number") return 1;
    if (typeof bv === "number") return -1;
    return av.localeCompare(bv);
  }
  return 0;
}

export function currentPackages(items: ExtensionPackage[]) {
  const current = new Map<string, ExtensionPackage>();
  for (const item of items) {
    if (["uninstalled", "rolledback"].includes(item.state.toLowerCase())) continue;
    const key = item.extensionId.toLowerCase();
    const existing = current.get(key);
    if (!existing || new Date(item.stagedAt ?? 0) >= new Date(existing.stagedAt ?? 0))
      current.set(key, item);
  }
  return [...current.values()];
}

export function availablePackages(store: ExtensionStoreItem[], installed: ExtensionPackage[]) {
  const versions = new Map(installed.map((item) => [item.extensionId.toLowerCase(), item.version]));
  return store.filter((item) => !versions.has(item.id.toLowerCase()) ||
    compareVersions(item.version, versions.get(item.id.toLowerCase())!) > 0);
}
