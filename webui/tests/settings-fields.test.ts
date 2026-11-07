import { describe, expect, it } from "vitest";
import { settingRestartNote, settingValue } from "../src/lib/settings-fields";

describe("Setting values", () => {
  it("separates restart notes consistently for descriptions and bare notes", () => {
    expect(`Only libraries your server allows.${settingRestartNote("Only libraries your server allows.")}`).toBe("Only libraries your server allows. · Restarts Allstarr");
    expect(settingRestartNote("")).toBe("Restarts Allstarr");
    expect(settingRestartNote("   ")).toBe("Restarts Allstarr");
    expect(settingRestartNote("Connection address")).toBe(" · Restarts Allstarr");
  });
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
