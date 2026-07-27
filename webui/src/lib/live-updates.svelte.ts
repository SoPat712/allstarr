export type LiveState = "connecting" | "live" | "reconnecting" | "stale";

type UpdateEvent = {
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

function scheduleStale() {
  if (staleTimer) clearTimeout(staleTimer);
  staleTimer = setTimeout(() => {
    state.status = "stale";
  }, 10_000);
}

export const liveUpdates = {
  state,
  connect() {
    if (source) return;

    state.status = "connecting";
    source = new EventSource("/api/admin/updates/stream");
    source.addEventListener("stream-status", () => {
      if (staleTimer) clearTimeout(staleTimer);
      state.status = "live";
      state.lastEventAt = new Date();
    });
    source.addEventListener("update", (message) => {
      const event = message as MessageEvent<string>;
      const update = JSON.parse(event.data) as UpdateEvent;
      const eventId = event.lastEventId || update.eventId || "";
      if (!acceptUpdate(update, eventId, revisions, seenEventIds)) return;
      state.revision += 1;
      state.lastEventAt = new Date();
      for (const listener of listeners) listener(update);
    });
    source.onerror = () => {
      state.status = "reconnecting";
      scheduleStale();
    };
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
