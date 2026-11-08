// Durations for Svelte transitions: zero when the viewer asked for reduced motion.
const reduced = typeof window !== "undefined" && typeof window.matchMedia === "function"
  ? window.matchMedia("(prefers-reduced-motion: reduce)") : null;

export const motion = (milliseconds: number) => reduced?.matches ? 0 : milliseconds;
