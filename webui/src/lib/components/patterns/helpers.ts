export type StatusTone = "neutral" | "good" | "warn" | "stop" | "info" | "accent" | "empty";
export type StatusSegment = { label: string; value: number; tone: StatusTone };
export type FilterOption = { id: string; label: string; count?: number };
type FilterStorage = Pick<Storage, "getItem" | "setItem">;

export function segmentShares(segments: readonly StatusSegment[]) {
  const values = segments.map(segment => Number.isFinite(segment.value) ? Math.max(0, segment.value) : 0);
  const total = values.reduce((sum, value) => sum + value, 0);
  return segments.map((segment, index) => ({
    ...segment,
    value: values[index],
    percentage: total ? values[index] / total * 100 : 0,
  }));
}

export function readFilter(key: string | undefined, ids: readonly string[], storage?: FilterStorage): string | undefined {
  if (!key) return undefined;
  try {
    const saved = (storage ?? globalThis.localStorage)?.getItem(key);
    return saved !== null && saved !== undefined && ids.includes(saved) ? saved : undefined;
  } catch {
    return undefined;
  }
}

export function writeFilter(key: string | undefined, value: string, storage?: FilterStorage): void {
  if (!key) return;
  try {
    (storage ?? globalThis.localStorage)?.setItem(key, value);
  } catch {
    // A filter still works when the browser blocks storage.
  }
}

export function nextFilterIndex(key: string, current: number, count: number): number | undefined {
  if (count < 1) return undefined;
  if (key === "Home") return 0;
  if (key === "End") return count - 1;
  const position = current >= 0 && current < count ? current : -1;
  if (key === "ArrowRight" || key === "ArrowDown") return (position + 1) % count;
  if (key === "ArrowLeft" || key === "ArrowUp") return position < 0 ? count - 1 : (position - 1 + count) % count;
  return undefined;
}

export function isTypingTarget(target: EventTarget | null): boolean {
  return typeof Element !== "undefined" && target instanceof Element && Boolean(target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="textbox"], [role="combobox"], [role="listbox"], [role="menu"], [role="slider"], [role="radiogroup"]'));
}
