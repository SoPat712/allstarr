import type { CoverDescription } from "./cover-for";
import { drawCover } from "./draw";
import { shouldAnimate } from "./motion";
import { retainWater } from "./water";

let fonts: Promise<unknown> | undefined;
export function mountCover(canvas: HTMLCanvasElement, getMix: () => CoverDescription) {
  const ctx = canvas.getContext("2d");
  if (!ctx) return { update() {}, destroy() {} };
  const release = retainWater(), preference = matchMedia("(prefers-reduced-motion: reduce)");
  const host = canvas.closest<HTMLElement>("button, a, [tabindex]") ?? canvas.parentElement!;
  let disposed = false, ready = false, onScreen = false, hovering = false, focused = host.matches(":focus-within");
  let raf = 0, start = 0, lastDraw = -Infinity, lastFrame = -1;
  function draw(t = 0) { if (ready && canvas.width > 0) drawCover(ctx!, canvas.width, getMix(), t); }
  function enabled() {
    return ready && shouldAnimate({ reduced: preference.matches, onScreen, pageVisible: !document.hidden, liquid: getMix().style === "liquid", engaged: hovering || focused });
  }
  function stop() { cancelAnimationFrame(raf); raf = 0; }
  function step(now: number) {
    raf = 0;
    if (disposed || !enabled()) return;
    const t = (now - start) / 1000 + 0.2, frame = Math.floor(t * 6) % 3;
    if (getMix().style === "liquid" ? now - lastDraw >= 1000 / 30 : frame !== lastFrame) {
      draw(t); lastDraw = now; lastFrame = frame;
    }
    raf = requestAnimationFrame(step);
  }
  function sync() {
    if (disposed) return;
    if (enabled()) {
      if (!raf) { start = performance.now(); lastDraw = -Infinity; lastFrame = -1; raf = requestAnimationFrame(step); }
    } else { stop(); draw(); }
  }
  function resize() {
    const size = Math.round(canvas.getBoundingClientRect().width * Math.min(2, window.devicePixelRatio || 1));
    if (size !== canvas.width) { canvas.width = canvas.height = size; draw(); }
  }
  const observer = new IntersectionObserver(entries => { onScreen = entries[0].isIntersecting; sync(); });
  const resizer = new ResizeObserver(resize);
  const enter = (event: PointerEvent) => { if (event.pointerType === "mouse") { hovering = true; sync(); } };
  const leave = () => { hovering = false; sync(); };
  const focus = () => { focused = true; sync(); };
  const unfocus = (event: FocusEvent) => { focused = event.relatedTarget instanceof Node && host.contains(event.relatedTarget); sync(); };
  host.addEventListener("pointerenter", enter); host.addEventListener("pointerleave", leave);
  host.addEventListener("focusin", focus); host.addEventListener("focusout", unfocus);
  preference.addEventListener("change", sync); document.addEventListener("visibilitychange", sync);
  window.addEventListener("resize", resize);
  observer.observe(canvas); resizer.observe(canvas); resize();
  fonts ??= Promise.all([document.fonts.load('700 40px "Archivo Narrow"'), document.fonts.load('400 40px "Courier Prime"'), document.fonts.load('700 40px "Courier Prime"')]).catch(() => {});
  void fonts.then(() => { if (!disposed) { ready = true; draw(); sync(); } });
  return {
    update() { if (!disposed) { stop(); resize(); draw(); sync(); } },
    destroy() {
      disposed = true; stop(); observer.disconnect(); resizer.disconnect();
      host.removeEventListener("pointerenter", enter); host.removeEventListener("pointerleave", leave);
      host.removeEventListener("focusin", focus); host.removeEventListener("focusout", unfocus);
      preference.removeEventListener("change", sync); document.removeEventListener("visibilitychange", sync);
      window.removeEventListener("resize", resize); canvas.width = canvas.height = 0; release();
    },
  };
}
