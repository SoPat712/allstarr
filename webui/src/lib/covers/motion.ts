export function shouldAnimate({ reduced, onScreen, pageVisible, liquid, engaged }: {
  reduced: boolean; onScreen: boolean; pageVisible: boolean; liquid: boolean; engaged: boolean;
}) {
  return !reduced && onScreen && pageVisible && (liquid || engaged);
}

// One page-wide clock, so a cover keeps its place through refreshes, scrolling and remounts.
// It turns around instead of wrapping, which never jumps and keeps shader precision in long-open tabs.
export function flowTime(milliseconds: number, turnAround = 20_000) {
  const seconds = (milliseconds / 1000 + 0.2) % (turnAround * 2);
  return seconds < turnAround ? seconds : turnAround * 2 - seconds;
}
