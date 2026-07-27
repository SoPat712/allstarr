export type LiveState = "connecting" | "live" | "reconnecting" | "stale";

type UpdateEvent = {
  revision?: number;
  resource?: string;
};

const state = $state({
  status: "connecting" as LiveState,
  revision: 0,
  lastEventAt: null as Date | null,
});

let source: EventSource | null = null;
let staleTimer: ReturnType<typeof setTimeout> | null = null;

function scheduleStale() {
  if (staleTimer) clearTimeout(staleTimer);
  staleTimer = setTimeout(() => {
    state.status = "stale";
  }, 10_000);
}

export const liveUpdates = {
  state,
  connect(onUpdate?: (event: UpdateEvent) => void) {
    if (source) return;

    state.status = "connecting";
    source = new EventSource("/api/admin/updates/stream");
    source.addEventListener("stream-status", () => {
      if (staleTimer) clearTimeout(staleTimer);
      state.status = "live";
      state.lastEventAt = new Date();
    });
    source.addEventListener("update", (message) => {
      const update = JSON.parse((message as MessageEvent<string>).data) as UpdateEvent;
      if (update.revision && update.revision <= state.revision) return;
      state.revision = update.revision ?? state.revision;
      state.lastEventAt = new Date();
      onUpdate?.(update);
    });
    source.onerror = () => {
      state.status = "reconnecting";
      scheduleStale();
    };
  },
  close() {
    source?.close();
    source = null;
    if (staleTimer) clearTimeout(staleTimer);
    staleTimer = null;
    state.status = "connecting";
  },
};
