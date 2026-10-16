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

it("reconnects with a fresh cursor when administrators change scope", () => {
  const opened: string[] = [];
  const closed = vi.fn();
  class Stream {
    constructor(url: string) { opened.push(url); }
    addEventListener() {}
    close() { closed(); }
  }
  vi.stubGlobal("EventSource", Stream);
  liveUpdates.connect(true, "mine");
  liveUpdates.setScope("everyone");
  expect(opened).toEqual(["/api/admin/updates/stream?scope=mine", "/api/admin/updates/stream?scope=everyone"]);
  expect(closed).toHaveBeenCalledOnce();
  liveUpdates.setScope("everyone");
  expect(opened).toHaveLength(2);
  liveUpdates.close();
  liveUpdates.connect(false, "everyone");
  liveUpdates.setScope("everyone");
  expect(opened.at(-1)).toBe("/api/admin/updates/stream?scope=mine");
  expect(opened).toHaveLength(3);
  liveUpdates.close();
  vi.unstubAllGlobals();
});

it("ignores queued messages from the previous household stream after switching to Mine", () => {
  const streams: Array<Map<string, (event: { data: string; lastEventId?: string }) => void>> = [];
  class Stream {
    handlers = new Map<string, (event: { data: string; lastEventId?: string }) => void>();
    constructor() { streams.push(this.handlers); }
    addEventListener(name: string, callback: (event: { data: string }) => void) { this.handlers.set(name, callback); }
    close() {}
  }
  vi.stubGlobal("EventSource", Stream);
  const received = vi.fn();
  const unsubscribe = liveUpdates.subscribe(received);
  liveUpdates.connect(true, "everyone");
  liveUpdates.setScope("mine");
  streams[0].get("update")?.({ data: '{"resource":"job","resourceId":"other"}', lastEventId: "old-event" });
  expect(received).not.toHaveBeenCalled();
  streams[1].get("update")?.({ data: '{"resource":"job","resourceId":"own"}', lastEventId: "new-event" });
  expect(received).toHaveBeenCalledOnce();
  unsubscribe();
  liveUpdates.close();
  vi.unstubAllGlobals();
});
