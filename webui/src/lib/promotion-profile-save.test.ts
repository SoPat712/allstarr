import { afterEach, expect, it, vi } from "vitest";
import { createProfileSave } from "./promotion-profile-save";

afterEach(() => vi.useRealTimers());

it("debounces edits separately for personal and household profiles and serializes writes", async () => {
  vi.useFakeTimers();
  let finish!: () => void;
  const save = vi.fn().mockImplementationOnce(() => new Promise<void>(resolve => { finish = resolve; })).mockResolvedValue(undefined);
  const queue = createProfileSave({ save, saved: vi.fn(), failed: vi.fn() });
  queue.change("me", "first"); queue.change("me", "second");
  await vi.advanceTimersByTimeAsync(500);
  queue.change("household", "shared"); queue.change("me", "third");
  await vi.advanceTimersByTimeAsync(500);
  expect(save.mock.calls).toEqual([["me", "second"]]);
  finish(); await vi.advanceTimersByTimeAsync(0);
  expect(save.mock.calls).toEqual([["me", "second"], ["household", "shared"], ["me", "third"]]);
});

it("cancels an obsolete pending script and flushes valid edits on leaving", async () => {
  vi.useFakeTimers();
  const save = vi.fn(async () => {});
  const queue = createProfileSave({ save, saved: vi.fn(), failed: vi.fn() });
  queue.change("me", "old"); queue.discard("me");
  await vi.advanceTimersByTimeAsync(600);
  expect(save).not.toHaveBeenCalled();
  queue.change("household", "valid"); await queue.flush();
  expect(save).toHaveBeenCalledExactlyOnceWith("household", "valid");
});

it("reports a failed write without dropping newer edits", async () => {
  vi.useFakeTimers();
  let reject!: (cause: Error) => void;
  const save = vi.fn().mockImplementationOnce(() => new Promise<void>((_, fail) => { reject = fail; })).mockResolvedValue(undefined), failed = vi.fn();
  const queue = createProfileSave({ save, saved: vi.fn(), failed });
  queue.change("me", "first"); await vi.advanceTimersByTimeAsync(500);
  queue.change("me", "newer"); reject(new Error("Unavailable"));
  await vi.advanceTimersByTimeAsync(500);
  expect(failed).toHaveBeenCalledOnce();
  expect(save.mock.calls).toEqual([["me", "first"], ["me", "newer"]]);
});
