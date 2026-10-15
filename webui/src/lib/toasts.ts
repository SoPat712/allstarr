import { readonly, writable } from "svelte/store";

export type ToastKind = "success" | "info" | "error";
export type ToastAction = { label: string; run: () => void | Promise<void> };
export type Toast = { id: number; message: string; kind: ToastKind; action?: ToastAction };
const store = writable<Toast[]>([]);
export const toasts = readonly(store);
let sequence = 0;
const lifetimes = new Map<number, { remaining: number; started: number; timer?: ReturnType<typeof setTimeout>; pauses: Set<string> }>();

function dismiss(id: number) {
  clearTimeout(lifetimes.get(id)?.timer);
  lifetimes.delete(id);
  store.update(items => items.filter(item => item.id !== id));
}
function start(id: number) {
  const life = lifetimes.get(id);
  if (!life || life.pauses.size) return;
  life.started = Date.now();
  life.timer = setTimeout(() => dismiss(id), life.remaining);
}
function show(message: string, kind: ToastKind = "info", action?: ToastAction) {
  const id = ++sequence;
  store.update(items => {
    if (items.length >= 3) {
      const oldest = items[0].id;
      clearTimeout(lifetimes.get(oldest)?.timer);
      lifetimes.delete(oldest);
    }
    return [...items.slice(-2), { id, message, kind, action }];
  });
  if (kind !== "error") {
    lifetimes.set(id, { remaining: 5000, started: Date.now(), pauses: new Set() });
    start(id);
  }
  return id;
}
export const toast = {
  show,
  success: (message: string, action?: ToastAction) => show(message, "success", action),
  info: (message: string, action?: ToastAction) => show(message, "info", action),
  error: (message: string, action?: ToastAction) => show(message, "error", action),
  dismiss,
  clear: () => {
    for (const life of lifetimes.values()) clearTimeout(life.timer);
    lifetimes.clear(); store.set([]);
  },
  pause: (id: number, reason: "hover" | "focus") => {
    const life = lifetimes.get(id);
    if (!life || life.pauses.has(reason)) return;
    if (!life.pauses.size) {
      clearTimeout(life.timer);
      life.remaining = Math.max(0, life.remaining - (Date.now() - life.started));
    }
    life.pauses.add(reason);
  },
  resume: (id: number, reason: "hover" | "focus") => {
    const life = lifetimes.get(id);
    if (life?.pauses.delete(reason) && !life.pauses.size) start(id);
  },
};
