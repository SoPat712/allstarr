import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { get } from "svelte/store";
import { toast, toasts } from "../src/lib/toasts";

describe("shared toast lifecycle", () => {
  beforeEach(() => { vi.useFakeTimers(); toast.clear(); });
  afterEach(() => { toast.clear(); vi.useRealTimers(); });

  it("keeps the newest three in arrival order and dismisses individual messages", () => {
    toast.info("Old"); toast.success("Second"); const third = toast.info("Third"); toast.error("Fourth");
    expect(get(toasts).map(item => item.message)).toEqual(["Second", "Third", "Fourth"]);
    toast.dismiss(third);
    expect(get(toasts).map(item => item.message)).toEqual(["Second", "Fourth"]);
  });

  it("expires success and info at five seconds while errors persist", () => {
    toast.success("Saved"); toast.info("Running"); toast.error("Retry needed");
    vi.advanceTimersByTime(4999);
    expect(get(toasts)).toHaveLength(3);
    vi.advanceTimersByTime(1);
    expect(get(toasts).map(item => item.message)).toEqual(["Retry needed"]);
  });

  it("keeps progress until it is dismissed", () => {
    const id = toast.progress("Testing");
    vi.advanceTimersByTime(600000);
    expect(get(toasts)).toEqual([{ id, message: "Testing", kind: "info", busy: true }]);
    toast.dismiss(id);
    expect(get(toasts)).toHaveLength(0);
  });

  it("pauses remaining time while either hover or focus is active", () => {
    const id = toast.success("Saved");
    vi.advanceTimersByTime(2000);
    toast.pause(id, "hover"); toast.pause(id, "focus");
    vi.advanceTimersByTime(10000);
    toast.resume(id, "hover"); vi.advanceTimersByTime(10000);
    expect(get(toasts)).toHaveLength(1);
    toast.resume(id, "focus"); vi.advanceTimersByTime(2999);
    expect(get(toasts)).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(get(toasts)).toHaveLength(0);
  });

  it("retains one action without invoking it until selected", async () => {
    const run = vi.fn();
    toast.error("Could not save", { label: "Retry", run });
    expect(run).not.toHaveBeenCalled();
    await get(toasts)[0].action?.run();
    expect(run).toHaveBeenCalledOnce();
  });

  it("clearing also cancels old timers before a new notification", () => {
    toast.success("First"); vi.advanceTimersByTime(2000); toast.clear();
    toast.success("Second"); vi.advanceTimersByTime(3000);
    expect(get(toasts).map(item => item.message)).toEqual(["Second"]);
  });
});
