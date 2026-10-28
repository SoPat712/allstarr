import { describe, expect, it } from "vitest";
import { alertDate, alertDeliveryLabel, destinationHost, parsePermittedHosts, thresholdDraft, thresholdsFromDraft } from "./alerts";

describe("alert delivery descriptions", () => {
  it("uses plain words for every delivery outcome", () => {
    expect(alertDeliveryLabel(undefined)).toBe("No deliveries yet");
    for (const [status, label] of Object.entries({ queued: "Waiting to send", sending: "Sending", delivered: "Delivered", retrying: "Retrying", failed: "Could not deliver", cancelled: "Cancelled", other: "Delivery pending" }))
      expect(alertDeliveryLabel({ status })).toBe(label);
  });
  it("handles absent and invalid dates without exposing an invalid date", () => {
    expect(alertDate(null)).toBe("Time unavailable");
    expect(alertDate("invalid")).toBe("Time unavailable");
    expect(alertDate("2026-10-28T12:00:00Z")).not.toBe("Time unavailable");
  });
});

describe("destination inputs", () => {
  it("keeps only a safe host hint and rejects unsupported or credentialed URLs", () => {
    expect(destinationHost(" https://notify.example:8443/private/topic?token=secret ")).toBe("notify.example:8443");
    for (const value of ["bad", "file:///private/topic", "https://name:secret@notify.example", "https://notify.example/topic#fragment"])
      expect(destinationHost(value)).toBe("");
  });
  it("accepts host names with optional ports, deduplicates, and rejects URL paths", () => {
    expect(parsePermittedHosts("notify.example\nNOTIFY.example\nwebhook.example:8443\n")).toEqual(["notify.example", "webhook.example:8443"]);
    expect(parsePermittedHosts("")).toEqual([]);
    for (const value of ["https://notify.example", "notify.example/path", "name@notify.example", "two hosts", "host#fragment", Array.from({ length: 33 }, (_, index) => `host${index}.example`).join("\n")])
      expect(parsePermittedHosts(value)).toBeNull();
  });
});

describe("disk thresholds", () => {
  const value = { warningBytes: 5 * 1024 ** 3, warningPercent: 5, criticalBytes: 1024 ** 3, criticalPercent: 1,
    hysteresisBytes: 1024 ** 3, hysteresisPercent: 1, cooldownMinutes: 30 };
  it("round trips bytes through readable GiB fields", () => {
    expect(thresholdDraft(value).warningGiB).toBe(5);
    expect(thresholdsFromDraft(thresholdDraft(value))).toEqual(value);
    expect(thresholdsFromDraft({ ...thresholdDraft(value), criticalGiB: 0.5 })?.criticalBytes).toBe(536870912);
  });
  it("rejects nonfinite, reversed, empty and out-of-range thresholds", () => {
    for (const changes of [{ warningGiB: NaN }, { warningGiB: undefined }, { criticalGiB: 6 }, { criticalPercent: 6 }, { hysteresisPercent: 51 }, { cooldownMinutes: 0 }, { cooldownMinutes: 1.5 }])
      expect(thresholdsFromDraft({ ...thresholdDraft(value), ...changes })).toBeNull();
  });
});
