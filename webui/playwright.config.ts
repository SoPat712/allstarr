import { existsSync } from "node:fs";
import { defineConfig } from "@playwright/test";

const port = Number(process.env.ALLSTARR_TEST_PORT ?? 4173);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error("Invalid browser test port.");
const preview = `npm run preview -- --host 127.0.0.1 --port ${port}`;
const webServerCommand = process.env.PLAYWRIGHT_SKIP_BUILD === "1" || existsSync("build/index.html")
  ? preview
  : `npm run build && ${preview}`;
const playwrightJsonOutput = process.env.PLAYWRIGHT_JSON_OUTPUT_FILE;
const reporter = playwrightJsonOutput
  ? [[process.env.CI ? "github" : "line"], ["json", { outputFile: playwrightJsonOutput }]]
  : process.env.CI ? "github" : "line";

export default defineConfig({
  testDir: "./tests",
  testMatch: "**/*.e2e.ts",
  fullyParallel: true,
  timeout: 120_000,
  expect: {
    timeout: 15_000,
  },
  reporter,
  use: {
    baseURL: `http://127.0.0.1:${port}/`,
    trace: "retain-on-failure",
  },
  webServer: {
    command: webServerCommand,
    url: `http://127.0.0.1:${port}/`,
    reuseExistingServer: !process.env.CI,
  },
});
