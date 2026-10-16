import { readActivityScope, type ActivityScope } from "./activity-scope";
import { adminUrl } from "./admin-url";

export type LiveState = "connecting" | "live" | "reconnecting" | "stale";

type UpdateEvent = {
  requiresAdministrator?: boolean;
  eventId?: string;
  revision?: number;
  resource?: string;
  resourceId?: string;
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
      const reconnecting = state.status === "reconnecting" || state.status === "stale";
      if (staleTimer) clearTimeout(staleTimer);
      state.status = "live";
      state.lastEventAt = new Date();
      if (reconnecting) for (const listener of listeners) listener({});
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
  subscribe(listener: (event: UpdateEvent) => void, pollInterval = 15_000) {
    listeners.add(listener);
    const fallback = setInterval(() => {
      if (state.status !== "live") listener({});
    }, pollInterval);
    return () => {
      listeners.delete(listener);
      clearInterval(fallback);
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
