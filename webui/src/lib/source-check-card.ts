import type { ProviderCheckHistory } from "./api";
import { relativeTime } from "./activity";
import { checkGuide, humanize } from "./sources";

export function sourceCheckCard(check: ProviderCheckHistory["checks"][number] | null) {
  if (!check) return null;
  return {
    capability: humanize(check.capability),
    account: check.accountLabel?.trim() || "Public",
    outcome: humanize(check.outcome),
    tone: check.outcome === "passed" ? "success" as const : check.outcome === "failed" ? "danger" as const : "neutral" as const,
    relative: relativeTime(check.testedAt, "Time unavailable"),
    absolute: Number.isFinite(Date.parse(check.testedAt))
      ? new Date(check.testedAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })
      : "Time unavailable",
    reason: check.outcome === "passed" ? null : checkGuide(check.reasonCode)?.label ??
      (check.outcome === "failed" ? "Check failed" : "Not checked"),
    latency: check.latencyMs != null && Number.isFinite(check.latencyMs) && check.latencyMs >= 0
      ? check.latencyMs === 0 ? "Under 1 ms" : `${Math.round(check.latencyMs)} ms`
      : "Latency unavailable",
  };
}
