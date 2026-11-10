import { afterEach, expect, it, vi } from "vitest";
import { activityWork, eventLog, playTraces } from "./api";
import { createActivityRefreshScheduler } from "./activity-requests";

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

it.each([
  ["history", () => eventLog.list({ limit: 100 }), { items: [], hasMore: false }],
  ["jobs", () => activityWork.jobs("history"), { jobs: [], hasMore: false }],
  ["job details", () => activityWork.detail("work"), { job: { id: "work" }, attempts: [] }],
  ["plays", () => playTraces.list(), []],
  ["play details", () => playTraces.detail("play"), { id: "play" }],
  ["tasks", () => activityWork.tasks(), { items: [], hasMore: false }],
] as const)("reuses unfinished %s reads and permits a later refresh", async (_name, read, body) => {
  let finish!: (response: Response) => void;
  const fetch = vi.fn(() => new Promise<Response>(resolve => { finish = resolve; }));
  vi.stubGlobal("fetch", fetch);
  const first = read(), second = read();
  expect(fetch).toHaveBeenCalledOnce();
  finish(new Response(JSON.stringify(body)));
  expect(await first).toEqual(await second);
  const next = read();
  expect(fetch).toHaveBeenCalledTimes(2);
  finish(new Response(JSON.stringify(body)));
  await next;
});

it("keeps filters, scope, and pagination independent while reusing a returning filter", async () => {
  const finish: Array<() => void> = [];
  const fetch = vi.fn(() => new Promise<Response>(resolve => {
    finish.push(() => resolve(new Response(JSON.stringify({ jobs: [], hasMore: false }))));
  }));
  vi.stubGlobal("fetch", fetch);
  const reads = [
    activityWork.jobs("history"),
    activityWork.jobs("history", "", "library.scan"),
    activityWork.jobs("history", "", "", "time", "cursor"),
    activityWork.jobs("history", "", "", "", "", "everyone"),
    activityWork.jobs("history"),
  ];
  expect(fetch).toHaveBeenCalledTimes(4);
  finish.forEach(resolve => resolve());
  await Promise.all(reads);
});

it("releases rejected reads so retries can fetch again", async () => {
  const fetch = vi.fn().mockRejectedValueOnce(new Error("offline"))
    .mockResolvedValueOnce(new Response(JSON.stringify({ items: [] })));
  vi.stubGlobal("fetch", fetch);
  const first = activityWork.tasks(), second = activityWork.tasks();
  await expect(first).rejects.toThrow("offline");
  await expect(second).rejects.toThrow("offline");
  await expect(activityWork.tasks()).resolves.toEqual({ items: [] });
  expect(fetch).toHaveBeenCalledTimes(2);
});

it("waits for slow refresh completion and skips work queued before the tab is hidden", async () => {
  vi.useFakeTimers();
  const doc = { visibilityState: "visible" };
  vi.stubGlobal("document", doc);
  let finish!: () => void;
  const refresh = vi.fn(() => new Promise<void>(resolve => { finish = resolve; }));
  const scheduler = createActivityRefreshScheduler(refresh);
  scheduler.schedule();
  await vi.advanceTimersByTimeAsync(250);
  expect(refresh).toHaveBeenCalledOnce();
  scheduler.schedule();
  await vi.advanceTimersByTimeAsync(15_000);
  expect(refresh).toHaveBeenCalledOnce();
  finish();
  await Promise.resolve();
  scheduler.schedule();
  doc.visibilityState = "hidden";
  await vi.advanceTimersByTimeAsync(250);
  scheduler.schedule();
  await vi.advanceTimersByTimeAsync(15_000);
  expect(refresh).toHaveBeenCalledOnce();
  doc.visibilityState = "visible";
  scheduler.schedule();
  await vi.advanceTimersByTimeAsync(250);
  expect(refresh).toHaveBeenCalledTimes(2);
  finish();
  await Promise.resolve();
  scheduler.schedule();
  scheduler.cancel();
  await vi.advanceTimersByTimeAsync(250);
  expect(refresh).toHaveBeenCalledTimes(2);
});

it("schedules each poll five seconds after the previous read completes", async () => {
  vi.useFakeTimers();
  const doc = Object.assign(new EventTarget(), { visibilityState: "visible" });
  vi.stubGlobal("document", doc);
  let finish!: () => void;
  const refresh = vi.fn(() => new Promise<void>(resolve => { finish = resolve; }));
  const scheduler = createActivityRefreshScheduler(refresh);
  const unsubscribe = scheduler.subscribe();
  expect(refresh).toHaveBeenCalledOnce();
  await vi.advanceTimersByTimeAsync(16_000);
  expect(refresh).toHaveBeenCalledOnce();
  finish();
  await vi.advanceTimersByTimeAsync(4_999);
  expect(refresh).toHaveBeenCalledOnce();
  await vi.advanceTimersByTimeAsync(1);
  expect(refresh).toHaveBeenCalledTimes(2);
  doc.visibilityState = "hidden";
  finish();
  await vi.advanceTimersByTimeAsync(10_000);
  expect(refresh).toHaveBeenCalledTimes(2);
  unsubscribe();
  scheduler.cancel();
  doc.visibilityState = "visible";
  await vi.advanceTimersByTimeAsync(10_000);
  expect(refresh).toHaveBeenCalledTimes(2);
});
