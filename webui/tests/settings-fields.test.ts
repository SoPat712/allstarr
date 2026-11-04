import { describe, expect, it } from "vitest";
import { settingValue } from "../src/lib/settings-fields";

describe("Setting values", () => {
  it("reads stored false strings as off and deployment values as readable text", () => {
    const field = { key: "CORS", label: "Browser access", type: "toggle", valuePath: "operatorSettings.Cors:AllowCredentials" };
    expect(settingValue({ operatorSettings: { "Cors:AllowCredentials": "false" } }, field)).toBe("false");
    expect(settingValue({ operatorSettings: { "Cors:AllowCredentials": "true" } }, field)).toBe("true");
    expect(settingValue({ operatorSettings: { "Cors:AllowCredentials": false } }, field)).toBe("false");
  });
  it("never places a saved secret in a replacement input", () => {
    expect(settingValue({ secret: "masked" }, { key: "KEY", label: "Key", type: "password", valuePath: "secret", sensitive: true })).toBe("");
  });
});
