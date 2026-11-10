import { json } from "./api-client";
import { adminUrl } from "./admin-url";
import { createRefreshScheduler, liveUpdates } from "./live-updates.svelte";

const reads = new Map<string, Promise<unknown>>();

export function readActivity<T>(url: string): Promise<T> {
  const key = adminUrl(url);
  const existing = reads.get(key);
  if (existing) return existing as Promise<T>;
  const pending = json<T>(key).finally(() => { reads.delete(key); });
  reads.set(key, pending);
  return pending;
}

export function createActivityRefreshScheduler(refresh: () => void | Promise<void>) {
  const scheduler = createRefreshScheduler(refresh, 250, 250);
  return {
    ...scheduler,
    subscribe() {
      let disposed = false;
      let timer: ReturnType<typeof setTimeout> | null = null;
      const unsubscribe = liveUpdates.subscribe(scheduler.schedule, false);
      async function poll() {
        try {
          if (typeof document === "undefined" || document.visibilityState !== "hidden") await refresh();
        } finally {
          if (!disposed) timer = setTimeout(() => { void poll(); }, 5_000);
        }
      }
      void poll();
      return () => {
        disposed = true;
        unsubscribe();
        if (timer !== null) clearTimeout(timer);
      };
    },
  };
}
