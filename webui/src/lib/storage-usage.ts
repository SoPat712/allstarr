import type { StatusSegment } from "$lib/components/patterns/helpers";

export function storageSegments(cached: number | null, kept: number | null, maximum: number, database: number | null): StatusSegment[] {
  if (cached === null || kept === null) return [];
  const mib = (bytes: number) => Math.round(bytes / 1024 ** 2 * 100) / 100;
  return [
    { label: "Cached songs (MiB)", value: mib(cached), tone: "info" },
    { label: "Kept songs (MiB)", value: mib(kept), tone: "good" },
    ...(database === null ? [] : [{ label: "Database (MiB)", value: mib(database), tone: "warn" as const }]),
    { label: "Cache room left (MiB)", value: mib(Math.max(0, maximum - cached)), tone: "neutral" },
  ];
}
