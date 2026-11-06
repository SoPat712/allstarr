import { afterEach, expect, it, vi } from "vitest";
import { createNamingPreview } from "./promotion-preview";

afterEach(() => vi.useRealTimers());

it("debounces typing and requests only the final script", async () => {
  vi.useFakeTimers();
  const preview = vi.fn(async (script: string) => script), received = vi.fn();
  const scheduler = createNamingPreview({ preview, received, failed: vi.fn() });
  scheduler.change("%artist%");
  await vi.advanceTimersByTimeAsync(200);
  scheduler.change("%artist%/%title%");
  await vi.advanceTimersByTimeAsync(299);
  expect(preview).not.toHaveBeenCalled();
  await vi.advanceTimersByTimeAsync(1);
  expect(preview).toHaveBeenCalledExactlyOnceWith("%artist%/%title%", expect.any(AbortSignal));
  expect(received).toHaveBeenCalledExactlyOnceWith("%artist%/%title%");
});

it("aborts old requests and ignores late results and failures", async () => {
  vi.useFakeTimers();
  let finish!: (value: string) => void, reject!: (error: Error) => void;
  const preview = vi.fn().mockImplementationOnce(() => new Promise<string>(resolve => { finish = resolve; }))
    .mockImplementationOnce(() => new Promise<string>((_, fail) => { reject = fail; })).mockResolvedValue("latest");
  const received = vi.fn(), failed = vi.fn();
  const scheduler = createNamingPreview({ preview, received, failed });
  scheduler.change("first"); await vi.advanceTimersByTimeAsync(300);
  scheduler.change("second");
  expect(preview.mock.calls[0][1].aborted).toBe(true);
  finish("stale"); await vi.advanceTimersByTimeAsync(300);
  scheduler.change("latest"); reject(new Error("stale failure"));
  await vi.advanceTimersByTimeAsync(300);
  expect(received).toHaveBeenCalledExactlyOnceWith("latest");
  expect(failed).not.toHaveBeenCalled();
});

it("cancels pending and running previews when leaving or changing profile", async () => {
  vi.useFakeTimers();
  const preview = vi.fn(async () => "result"), received = vi.fn();
  const scheduler = createNamingPreview({ preview, received, failed: vi.fn() });
  scheduler.change("pending"); scheduler.cancel();
  await vi.advanceTimersByTimeAsync(400);
  expect(preview).not.toHaveBeenCalled();
});

it("reports current request failures", async () => {
  vi.useFakeTimers();
  const cause = new Error("Examples unavailable"), failed = vi.fn();
  const scheduler = createNamingPreview({ preview: async () => { throw cause; }, received: vi.fn(), failed });
  scheduler.change("%title%"); await vi.advanceTimersByTimeAsync(300);
  expect(failed).toHaveBeenCalledExactlyOnceWith(cause);
});
