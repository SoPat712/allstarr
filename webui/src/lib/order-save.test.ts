import { afterEach, expect, it, vi } from "vitest";
import { createOrderSave } from "./order-save";

afterEach(() => vi.useRealTimers());

it("debounces repeated moves and keeps the previous saved order for undo", async () => {
  vi.useFakeTimers();
  const save = vi.fn(async () => {}), saved = vi.fn();
  const orders = createOrderSave({ save, saved, failed: vi.fn() });
  orders.change("search", ["b", "a", "c"], ["a", "b", "c"]);
  await vi.advanceTimersByTimeAsync(600);
  orders.change("search", ["b", "c", "a"], ["b", "a", "c"]);
  await vi.advanceTimersByTimeAsync(799);
  expect(save).not.toHaveBeenCalled();
  await vi.advanceTimersByTimeAsync(1);
  expect(save).toHaveBeenCalledExactlyOnceWith("search", ["b", "c", "a"]);
  expect(saved).toHaveBeenCalledExactlyOnceWith("search", ["b", "c", "a"], ["a", "b", "c"]);
});

it("serializes lists and saves moves made during an in-flight write", async () => {
  vi.useFakeTimers();
  let finish!: () => void;
  const save = vi.fn().mockImplementationOnce(() => new Promise<void>(resolve => { finish = resolve; })).mockResolvedValue(undefined);
  const saved = vi.fn();
  const orders = createOrderSave({ save, saved, failed: vi.fn() });
  orders.change("search", ["b", "a"], ["a", "b"]);
  await vi.advanceTimersByTimeAsync(800);
  orders.change("search", ["a", "b"], ["b", "a"]);
  orders.change("lyrics", ["kept", "backend"], ["backend", "kept"]);
  await vi.advanceTimersByTimeAsync(800);
  expect(save).toHaveBeenCalledTimes(1);
  finish();
  await vi.advanceTimersByTimeAsync(0);
  expect(save.mock.calls).toEqual([["search", ["b", "a"]], ["search", ["a", "b"]], ["lyrics", ["kept", "backend"]]]);
  expect(saved.mock.calls[1]).toEqual(["search", ["a", "b"], ["b", "a"]]);
});

it("drops pending moves for a failed list so stale order cannot overwrite a reload", async () => {
  vi.useFakeTimers();
  let fail!: (cause: Error) => void;
  const save = vi.fn(() => new Promise<void>((_, reject) => { fail = reject; })), failed = vi.fn();
  const orders = createOrderSave({ save, saved: vi.fn(), failed });
  orders.change("search", ["b", "a"], ["a", "b"]);
  await vi.advanceTimersByTimeAsync(800);
  orders.change("search", ["a", "b"], ["b", "a"]);
  const conflict = new Error("Order changed in another session.");
  fail(conflict);
  await vi.advanceTimersByTimeAsync(1600);
  expect(save).toHaveBeenCalledTimes(1);
  expect(failed).toHaveBeenCalledExactlyOnceWith("search", conflict);
});

it("flushes pending orders when leaving the view", async () => {
  vi.useFakeTimers();
  const save = vi.fn(async () => {});
  const orders = createOrderSave({ save, saved: vi.fn(), failed: vi.fn() });
  orders.change("search", ["b", "a"], ["a", "b"]);
  orders.flush();
  await vi.advanceTimersByTimeAsync(0);
  expect(save).toHaveBeenCalledExactlyOnceWith("search", ["b", "a"]);
});
