import { existsSync } from "node:fs";
import { defineConfig } from "@playwright/test";

const preview = "npm run preview -- --host 127.0.0.1 --port 4173";
const webServerCommand = process.env.PLAYWRIGHT_SKIP_BUILD === "1" || existsSync("build/index.html")
  ? preview
  : `npm run build && ${preview}`;

export default defineConfig({
  testDir: "./tests",
  testMatch: "**/*.e2e.ts",
  fullyParallel: true,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? "github" : "line",
  use: {
    baseURL: "http://127.0.0.1:4173/",
    trace: "retain-on-failure",
  },
  webServer: {
    command: webServerCommand,
    url: "http://127.0.0.1:4173/",
    reuseExistingServer: !process.env.CI,
  },
});
