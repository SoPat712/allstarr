import { describe, expect, it } from "vitest";
import { countLabel, countNoun } from "./count-label";

describe("count labels", () => {
  it.each([0, 1, 2, 1000])("formats %s with the right noun", count => {
    expect(countLabel(count, "check")).toBe(`${count.toLocaleString("en-US")} ${count === 1 ? "check" : "checks"}`);
  });

  it("keeps noun captions in step with counts shown separately", () => {
    expect(countNoun(1, "Play")).toBe("Play");
    expect(countNoun(0, "Play")).toBe("Plays");
    expect(countNoun(2, "Play")).toBe("Plays");
    expect(countNoun(1, "copy", "copies")).toBe("copy");
    expect(countNoun(2, "copy", "copies")).toBe("copies");
  });

  it("supports irregular nouns and phrases with verbs", () => {
    expect(countLabel(1, "copy", "copies")).toBe("1 copy");
    expect(countLabel(2, "copy", "copies")).toBe("2 copies");
    expect(countLabel(1, "source is failing checks", "sources are failing checks")).toBe("1 source is failing checks");
    expect(countLabel(2, "source is failing checks", "sources are failing checks")).toBe("2 sources are failing checks");
    expect(countLabel(1, "more thing", "more things")).toBe("1 more thing");
    expect(countLabel(1, "slow start")).toBe("1 slow start");
  });
});
