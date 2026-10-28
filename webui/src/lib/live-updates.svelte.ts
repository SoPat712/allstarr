import { readActivityScope, type ActivityScope } from "./activity-scope";
import { adminUrl } from "./admin-url";

export type LiveState = "connecting" | "live" | "reconnecting" | "stale";

export type UpdateEvent = {
  reason?: "poll" | "resume";
  requiresAdministrator?: boolean;
  eventId?: string;
  revision?: number;
  resource?: string;
  resourceId?: string;
  data?: { category?: string; type?: string };
};

const state = $state({
  status: "connecting" as LiveState,
  revision: 0,
  lastEventAt: null as Date | null,
});

let source: EventSource | null = null;
let connectedAdministrator = false;
let connectedScope: ActivityScope = "mine";
let staleTimer: ReturnType<typeof setTimeout> | null = null;
const listeners = new Set<(event: UpdateEvent) => void>();
const revisions = new Map<string, number>();
const seenEventIds = new Set<string>();

export function acceptUpdate(
  update: UpdateEvent,
  eventId: string,
  knownRevisions: Map<string, number>,
  knownEventIds: Set<string>,
) {
  if (eventId && knownEventIds.has(eventId)) return false;

  const key = update.resource && update.resourceId
    ? `${update.resource}:${update.resourceId}`
    : "";
  if (key && update.revision !== undefined) {
    const known = knownRevisions.get(key);
    if (known !== undefined && update.revision < known) return false;
    knownRevisions.set(key, update.revision);
    if (knownRevisions.size > 1_000) {
      knownRevisions.delete(knownRevisions.keys().next().value!);
    }
  }

  if (eventId) {
    knownEventIds.add(eventId);
    if (knownEventIds.size > 1_000) {
      knownEventIds.delete(knownEventIds.values().next().value!);
    }
  }
  return true;
}

export function createRefreshScheduler(
  refresh: () => void | Promise<void>,
  delay = 250,
) {
  let timer: ReturnType<typeof setTimeout> | null = null;
  return {
    schedule() {
      if (timer) return;
      timer = setTimeout(() => {
        timer = null;
        void refresh();
      }, delay);
    },
    cancel() {
      if (timer) clearTimeout(timer);
      timer = null;
    },
  };
}

export function createManualRefresh() {
  let pending = $state(false);
  return {
    get pending() { return pending; },
    async run(refresh: () => void | Promise<unknown>) {
      if (pending) return;
      pending = true;
      try { await refresh(); }
      finally { pending = false; }
    },
  };
}

function scheduleStale() {
  if (staleTimer) clearTimeout(staleTimer);
  staleTimer = setTimeout(() => {
    state.status = "stale";
  }, 10_000);
}

export const liveUpdates = {
  state,
  connect(administrator = false, scope = readActivityScope(administrator)) {
    if (source) return;

    connectedAdministrator = administrator;
    connectedScope = administrator ? scope : "mine";
    state.status = "connecting";
    const stream = new EventSource(adminUrl(`/api/admin/updates/stream?scope=${connectedScope}`));
    source = stream;
    stream.addEventListener("stream-status", () => {
      if (source !== stream) return;
      if (staleTimer) clearTimeout(staleTimer);
      revisions.clear();
      seenEventIds.clear();
      state.status = "live";
      state.lastEventAt = new Date();
      for (const listener of listeners) listener({});
    });
    stream.addEventListener("update", (message) => {
      if (source !== stream) return;
      const event = message as MessageEvent<string>;
      let update: UpdateEvent;
      try {
        const parsed = JSON.parse(event.data);
        if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return;
        update = parsed as UpdateEvent;
      } catch { return; }
      const eventId = event.lastEventId || update.eventId || "";
      if (!acceptUpdate(update, eventId, revisions, seenEventIds)) return;
      state.revision += 1;
      state.lastEventAt = new Date();
      for (const listener of listeners) listener(update);
    });
    stream.onerror = () => {
      if (source !== stream) return;
      state.status = "reconnecting";
      scheduleStale();
    };
  },
  setScope(scope: ActivityScope) {
    if (!source || !connectedAdministrator || scope === connectedScope) return;
    const administrator = connectedAdministrator;
    this.close();
    this.connect(administrator, scope);
  },
  subscribe(listener: (event: UpdateEvent) => void, pollInterval = 5_000) {
    listeners.add(listener);
    // Some changes have no outbox event. Reconcile visible pages even on a healthy stream.
    const reconcile = (reason: "poll" | "resume") => {
      if (typeof document === "undefined" || document.visibilityState !== "hidden") listener({ reason });
    };
    const resume = () => reconcile("resume");
    const fallback = setInterval(() => reconcile("poll"), pollInterval);
    if (typeof window !== "undefined") window.addEventListener("focus", resume);
    if (typeof document !== "undefined") document.addEventListener("visibilitychange", resume);
    return () => {
      listeners.delete(listener);
      clearInterval(fallback);
      if (typeof window !== "undefined") window.removeEventListener("focus", resume);
      if (typeof document !== "undefined") document.removeEventListener("visibilitychange", resume);
    };
  },
  close() {
    source?.close();
    source = null;
    if (staleTimer) clearTimeout(staleTimer);
    staleTimer = null;
    revisions.clear();
    seenEventIds.clear();
    state.status = "connecting";
  },
};
