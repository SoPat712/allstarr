import { describe, expect, it } from "vitest";
import { formatLocalDateTime } from "./dates";

describe("local timestamps", () => {
  it("shows a short date and time in the selected time zone", () => {
    expect(formatLocalDateTime("2026-11-06T00:45:59Z", undefined, {
      locale: "en-US", timeZone: "America/New_York",
    })).toBe("Nov 5, 7:45 PM");
    expect(formatLocalDateTime("2026-11-06T00:45:59Z", undefined, {
      locale: "en-US", timeZone: "Asia/Tokyo",
    })).toBe("Nov 6, 9:45 AM");
  });

  it("uses the local time zone when none is selected", () => {
    const value = new Date(2026, 10, 5, 19, 45, 59).toISOString();
    expect(formatLocalDateTime(value, undefined, { locale: "en-US" })).toBe("Nov 5, 7:45 PM");
  });

  it("honors the selected locale without adding a year or seconds", () => {
    expect(formatLocalDateTime("2026-11-06T00:45:59Z", undefined, {
      locale: "en-GB", timeZone: "America/New_York",
    })).toBe("5 Nov, 19:45");
  });

  it("keeps the caller's wording for missing and invalid values", () => {
    for (const value of [undefined, null, "", "invalid", "2026-99-99T00:00:00Z"]) {
      expect(formatLocalDateTime(value)).toBe("Time unavailable");
      expect(formatLocalDateTime(value, "Not recorded")).toBe("Not recorded");
      expect(formatLocalDateTime(value, "")).toBe("");
    }
  });
});
