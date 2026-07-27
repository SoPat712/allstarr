import type { JobProgress } from "./api";

export type JobProgressDetails = {
  stage?: string;
  message?: string;
  completed?: number | null;
  total?: number | null;
  provider?: string | null;
  playlist?: string | null;
  track?: string | null;
  deferralReason?: string | null;
  throughputPerSecond?: number | null;
};

export function progressDetails(item: JobProgress): JobProgressDetails {
  try {
    const value = JSON.parse(item.detailsJson);
    return value && typeof value === "object" ? value : {};
  } catch {
    return {};
  }
}

export function compactProgress(items: JobProgress[]) {
  let previous = "";
  return items.filter((item) => {
    const details = progressDetails(item);
    const key = `${item.action}|${item.outcome}|${details.message ?? ""}`;
    if (key === previous) return false;
    previous = key;
    return true;
  }).slice(0, 200);
}
