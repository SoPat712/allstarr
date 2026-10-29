import type { TasteNudge } from "./api";

export function weightDescription(weight: number): string {
  return weight >= 0.5
    ? "Right now, what you've played today counts most."
    : "Not much recent listening, so your usual taste counts most.";
}

export function nudgeLabel(nudge: TasteNudge): string {
  const label = nudge.mode === "never" ? `Never: ${nudge.name}` : `${nudge.mode === "more" ? "More" : "Less"} ${nudge.name}`;
  return `${label}${nudge.expiresAt ? " · today only" : ""}`;
}
