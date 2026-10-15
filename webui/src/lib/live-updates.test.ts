import { afterEach, describe, expect, it, vi } from "vitest";
import { acceptUpdate, createRefreshScheduler, liveUpdates } from "./live-updates.svelte";

afterEach(() => {
  liveUpdates.close();
  vi.useRealTimers();
});

describe("acceptUpdate", () => {
  it("deduplicates event IDs without comparing unrelated resource revisions", () => {
    const revisions = new Map<string, number>();
    const eventIds = new Set<string>();

    expect(
      acceptUpdate(
        { resource: "job", resourceId: "job-1", revision: 5 },
        "event-1",
        revisions,
        eventIds,
      ),
    ).toBe(true);
    expect(
      acceptUpdate(
        { resource: "track-match", resourceId: "match-1", revision: 1 },
        "event-2",
        revisions,
        eventIds,
      ),
    ).toBe(true);
    expect(
      acceptUpdate(
        { resource: "job", resourceId: "job-1", revision: 4 },
        "event-3",
        revisions,
        eventIds,
      ),
    ).toBe(false);
    expect(
      acceptUpdate(
        { resource: "job", resourceId: "job-1", revision: 5 },
        "event-1",
        revisions,
        eventIds,
      ),
    ).toBe(false);
  });

  it("polls only while the unified stream is unavailable", () => {
    vi.useFakeTimers();
    const listener = vi.fn();
    const unsubscribe = liveUpdates.subscribe(listener, 1_000);

    liveUpdates.state.status = "reconnecting";
    vi.advanceTimersByTime(1_000);
    expect(listener).toHaveBeenCalledOnce();

    liveUpdates.state.status = "live";
    vi.advanceTimersByTime(1_000);
    expect(listener).toHaveBeenCalledOnce();

    unsubscribe();
    liveUpdates.state.status = "stale";
    vi.advanceTimersByTime(1_000);
    expect(listener).toHaveBeenCalledOnce();
  });

  it("coalesces refreshes and cancels pending work", () => {
    vi.useFakeTimers();
    const refresh = vi.fn();
    const scheduler = createRefreshScheduler(refresh, 250);

    scheduler.schedule();
    scheduler.schedule();
    vi.advanceTimersByTime(250);
    expect(refresh).toHaveBeenCalledOnce();

    scheduler.schedule();
    scheduler.cancel();
    vi.advanceTimersByTime(250);
    expect(refresh).toHaveBeenCalledOnce();
  });
});

it("refreshes subscribers after reconnect and ignores malformed stream messages", () => {
  const handlers = new Map<string, (event: { data: string }) => void>();
  let fail: (() => void) | undefined;
  class Stream {
    set onerror(value: () => void) { fail = value; }
    addEventListener(name: string, listener: (event: { data: string }) => void) { handlers.set(name, listener); }
    close() {}
  }
  vi.stubGlobal("EventSource", Stream);
  const listener = vi.fn();
  const unsubscribe = liveUpdates.subscribe(listener);
  liveUpdates.connect();
  handlers.get("stream-status")?.({ data: "{}" });
  expect(listener).not.toHaveBeenCalled();
  fail?.();
  handlers.get("stream-status")?.({ data: "{}" });
  expect(listener).toHaveBeenCalledOnce();
  expect(() => handlers.get("update")?.({ data: "invalid-json" })).not.toThrow();
  unsubscribe();
  liveUpdates.close();
  vi.unstubAllGlobals();
});
