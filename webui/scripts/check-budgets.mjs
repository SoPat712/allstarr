import { readFileSync } from "node:fs";
import { gzipSync } from "node:zlib";

const root = new URL("../.svelte-kit/output/client/", import.meta.url);
const manifest = JSON.parse(readFileSync(new URL(".vite/manifest.json", root)));
const find = (pattern) => Object.keys(manifest).find((key) => pattern.test(key));
const roots = [
  find(/generated\/client-optimized\/app\.js$/),
  find(/runtime\/client\/entry\.js$/),
  find(/generated\/client-optimized\/nodes\/0\.js$/),
  find(/generated\/client-optimized\/nodes\/1\.js$/),
  find(/generated\/client-optimized\/nodes\/2\.js$/),
];

if (roots.some((key) => !key)) throw new Error("Could not find the SvelteKit entry chunks.");

function closure(entries) {
  const found = new Set();
  const visit = (key) => {
    if (found.has(key) || !manifest[key]) return;
    found.add(key);
    manifest[key].imports?.forEach(visit);
  };
  entries.forEach(visit);
  return found;
}

const gzipBytes = (key) => gzipSync(readFileSync(new URL(manifest[key].file, root))).length;
const initial = closure(roots);
const initialJs = [...initial].reduce((total, key) => total + gzipBytes(key), 0);
const initialCss = new Set([...initial].flatMap((key) => manifest[key].css ?? []));
const cssBytes = [...initialCss].reduce(
  (total, file) => total + gzipSync(readFileSync(new URL(file, root))).length,
  0,
);
const page = manifest[roots[4]];
const routes = (page.dynamicImports ?? []).map((key) => {
  const files = [...closure([key])].filter((chunk) => !initial.has(chunk));
  return [key, files.reduce((total, chunk) => total + gzipBytes(chunk), 0)];
});
const kib = (bytes) => `${(bytes / 1024).toFixed(1)} KiB`;
const failures = [
  initialJs > 100 * 1024 && `initial JavaScript ${kib(initialJs)} > 100 KiB`,
  cssBytes > 30 * 1024 && `initial CSS ${kib(cssBytes)} > 30 KiB`,
  ...routes
    .filter(([, bytes]) => bytes > 100 * 1024)
    .map(([key, bytes]) => `${key} ${kib(bytes)} > 100 KiB`),
].filter(Boolean);

console.log(`Initial JavaScript: ${kib(initialJs)}; CSS: ${kib(cssBytes)}`);
for (const [key, bytes] of routes) console.log(`${key}: ${kib(bytes)}`);
if (failures.length) throw new Error(`Frontend budgets exceeded:\n${failures.join("\n")}`);
