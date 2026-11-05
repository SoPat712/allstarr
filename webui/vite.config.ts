import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { sveltekit } from "@sveltejs/kit/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

const versionSource = readFileSync(
  fileURLToPath(new URL("../allstarr/AppVersion.cs", import.meta.url)),
  "utf8",
);
const appVersion = versionSource.match(/Version = "([^"]+)"/)?.[1];

if (!appVersion) {
  throw new Error("Could not read the canonical Allstarr version.");
}

export default defineConfig({
  plugins: [tailwindcss(), sveltekit()],
  define: {
    __APP_VERSION__: JSON.stringify(appVersion),
  },
  server: {
    proxy: {
      "/api": "http://127.0.0.1:5275",
      "/fonts": "http://127.0.0.1:5275",
      "/images": "http://127.0.0.1:5275",
      "/favicon.svg": "http://127.0.0.1:5275",
    },
  },
});
