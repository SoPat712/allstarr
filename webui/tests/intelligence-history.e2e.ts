import { createServer } from "node:http";
import { expect, test, type Page } from "@playwright/test";
import type { HistoryImport, ListeningStats } from "../src/lib/api";
import { appearance, capture, mockIntelligence } from "./intelligence-fixtures";

const root = "/api/admin/history/imports";
const id = "44444444-4444-4444-8444-444444444444";
const file = { name: "history.zip", mimeType: "application/zip", buffer: Buffer.from("history fixture") };
const preview: HistoryImport = {
  importId: id, revision: "a".repeat(64), displayFileName: file.name, sizeBytes: file.buffer.length,
  expiresAt: "2026-10-29T12:00:00Z", state: 0, jobId: null, jobState: null, lastErrorCode: null, lastErrorMessage: null,
  importedRows: 0, duplicateRows: 0, resolvedRows: 0, unresolvedRows: 0,
  preview: { format: "spotify-extended-streaming-history", fileRows: 50012, musicRows: 50000, completed: 48210, partial: 290, skipped: 1500,
    episodes: 8, nonTrack: 0, malformed: 4, duplicateInFile: 0, duplicateExisting: 0, newRows: 50000, resolvedNewRows: 49900, unresolvedNewRows: 100,
    rowsWithoutProviderIdentity: 2, sourceUserCount: 1, estimatedMusicBrainzLookups: 100, earliest: "2016-01-01T00:00:00Z", latest: "2026-10-28T00:00:00Z", reasonCounts: {}, outsideRetentionRows: 0, songs: 3210 },
};
const batchKey = "allstarr.history-import-batches";
const batchFiles = ["Streaming_History_Audio_2016-2019.json", "Streaming_History_Audio_2020-2023.json", "Streaming_History_Audio_2024-2026.json"]
  .map(name => ({ name, mimeType: "application/json", buffer: Buffer.from("history fixture") }));
const batchDates = [
  ["2016-01-01T00:00:00Z", "2019-12-31T00:00:00Z"],
  ["2020-01-01T00:00:00Z", "2023-12-31T00:00:00Z"],
  ["2024-01-01T00:00:00Z", "2026-10-28T00:00:00Z"],
];
const batchIds = batchFiles.map((_, index) => `44444444-4444-4444-8444-${444444444444 + index}`);
type UploadResponse = { status?: number; body?: unknown; gate?: Promise<void>; item?: HistoryImport };
const stats: ListeningStats = {
  period: { from: null, to: "2026-10-28T12:00:00Z", timeZoneId: "UTC" }, playCount: 24, activeDays: 2, importedCount: 0,
  observedSince: null, estimatedTimeFromSongLengthsMilliseconds: 3600000, coverage: { unknownSongIdentityCount: 0, unknownDurationCount: 0 }, topSongs: [], topArtists: [], topAlbums: [],
};

async function mockHistory(page: Page, items: HistoryImport[] = []) {
  await mockIntelligence(page, { stats });
  const state = { items: structuredClone(items), confirms: [] as unknown[], confirmIds: [] as string[], deletes: [] as string[], deleteIds: [] as string[], uploads: [] as { query: string; body: Buffer | null; length: string | undefined; contentType: string | undefined }[],
    responses: {} as Record<string, UploadResponse>, activeUploads: 0, maxActiveUploads: 0, listLimit: Infinity,
    reads: 0, statsReads: 0, uploadStatus: 200, uploadBody: {} as unknown, uploadGate: null as Promise<void> | null, networkFailure: false, progress: false, complete: false, pollFailure: false, deleteFailure: false, confirmFailure: false };
  await page.route("**/api/admin/stats?*", route => { state.statsReads++; return route.fulfill({ json: { ...stats, importedCount: state.items.filter(item => item.state === 3).reduce((count, item) => count + item.preview.completed, 0) } }); });
  await page.route("**/api/admin/history/imports**", async route => {
    const request = route.request(), url = new URL(request.url());
    if (!["localhost", "127.0.0.1"].includes(url.hostname)) return route.abort();
    if (url.pathname === root && request.method() === "POST") {
      const headers = await request.allHeaders();
      state.uploads.push({ query: url.search, body: request.postDataBuffer(), length: headers["content-length"], contentType: headers["content-type"] });
      const name = url.searchParams.get("fileName") ?? file.name;
      const response = state.responses[name];
      const uploadIndex = state.uploads.length - 1;
      state.activeUploads++;
      state.maxActiveUploads = Math.max(state.maxActiveUploads, state.activeUploads);
      try {
        await (response?.gate ?? state.uploadGate);
        if (state.networkFailure) return await route.abort();
        const status = response?.status ?? state.uploadStatus;
        if (status !== 200) return await route.fulfill({ status, json: response?.body ?? state.uploadBody });
        const item = structuredClone(response?.item ?? { ...preview, importId: `44444444-4444-4444-8444-${444444444444 + uploadIndex}`, displayFileName: name });
        state.items.unshift(item);
        return await route.fulfill({ json: item });
      } finally { state.activeUploads--; }
    }
    if (url.pathname === root) return route.fulfill({ json: state.items.slice(0, state.listLimit) });
    const targetId = url.pathname.slice(root.length + 1).split("/")[0];
    if (request.method() === "DELETE") {
      state.deletes.push(url.search); state.deleteIds.push(targetId);
      if (state.deleteFailure) return route.fulfill({ status: 409, json: { error: "history_import_conflict", message: "The import changed. Refresh and try again." } });
      state.items = state.items.filter(item => item.importId !== targetId);
      return route.fulfill({ json: { removedListens: 50000 } });
    }
    if (url.pathname === `${root}/${targetId}/confirm` && request.method() === "POST") {
      state.confirms.push(request.postDataJSON()); state.confirmIds.push(targetId);
      if (state.confirmFailure) return route.fulfill({ status: 409, json: { error: "history_import_conflict", message: "The preview has expired. Choose the file again." } });
      const index = state.items.findIndex(item => item.importId === targetId);
      if (index < 0) return route.fulfill({ status: 404 });
      state.items[index] = { ...state.items[index], state: 1, jobId: `job-${targetId}`, jobState: "pending" };
      return route.fulfill({ json: state.items[index] });
    }
    state.reads++;
    if (state.pollFailure) return route.fulfill({ status: 503, json: { error: "Unavailable" } });
    const index = state.items.findIndex(item => url.pathname.endsWith(item.importId));
    if (index < 0) return route.fulfill({ status: 404 });
    if (state.progress && state.items[index].state === 1) state.items[index] = { ...state.items[index], state: 2, importedRows: 21000 };
    if (state.complete && state.items[index].state === 2) state.items[index] = { ...state.items[index], state: 3, importedRows: 50000 };
    return route.fulfill({ json: state.items[index] });
  });
  return state;
}

async function openHistory(page: Page) {
  await page.goto("/#/intelligence?section=stats");
  await expect(page.getByRole("heading", { name: "Listening history from other services", exact: true })).toBeVisible();
}
async function upload(page: Page, files: typeof file | (typeof file)[] = file) {
  const chooser = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Import history", exact: true }).click();
  const picker = await chooser;
  expect(picker.isMultiple()).toBe(true);
  await picker.setFiles(files);
  return page.getByRole("dialog", { name: "Import history", exact: true });
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`History card, preview and delete dialog at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await mockHistory(page, [
      { ...preview, importId: "88888888-8888-4888-8888-888888888888", state: 2, importedRows: 21000 },
      { ...preview, importId: "55555555-5555-4555-8555-555555555555", state: 1, preview: { ...preview.preview, format: "lastfm-recent-tracks" } },
      { ...preview, importId: "66666666-6666-4666-8666-666666666666", state: 3, importedRows: 50000, preview: { ...preview.preview, format: "listenbrainz-export" } },
      { ...preview, importId: "77777777-7777-4777-8777-777777777777", state: 5, lastErrorMessage: "The history file is no longer available.", preview: { ...preview.preview, format: "maloja-export" } },
    ]);
    await openHistory(page);
    const card = page.getByRole("region", { name: "Listening history from other services", exact: true });
    await expect(card).toContainText("Importing 42%");
    await expect(card).toContainText("Waiting");
    await expect(card).toContainText("Done");
    await expect(card).toContainText("Failed: The history file is no longer available.");
    await expect(card.getByRole("listitem").first()).toContainText("Spotify");
    await expect(card).toContainText("Import date unavailable");
    await expect(card).toContainText("Bring in your history from Spotify, Last.fm, ListenBrainz, Maloja or Koito. Imported plays show separately and never scrobble.");
    await capture(page, "history", width, theme);
    let finishUpload!: () => void;
    state.uploadGate = new Promise(resolve => { finishUpload = resolve; });
    const dialog = await upload(page);
    await expect(page.getByRole("progressbar", { name: "History upload", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Import history", exact: true })).toBeDisabled();
    await capture(page, "history-upload", width, theme);
    finishUpload();
    await expect(dialog).toContainText("Spotify");
    await expect(dialog).toContainText("Jan 1, 2016 – Oct 28, 2026");
    await expect(dialog).toContainText("48,210 plays");
    await expect(dialog).toContainText("1,500 skips");
    await expect(dialog).toContainText("3,210 songs");
    await expect(dialog).toContainText("4 rows not recognized");
    await capture(page, "history-preview", width, theme);
    await expect(dialog.getByRole("button", { name: "Cancel", exact: true })).toHaveAttribute("type", "button");
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(dialog).toBeHidden();
    expect(state.confirms).toEqual([]);
    await expect.poll(() => state.deleteIds).toEqual([id]);
    await card.getByRole("button", { name: "Delete", exact: true }).first().click();
    const deletion = page.getByRole("alertdialog", { name: "Delete this import?", exact: true });
    await expect(deletion).toContainText("Its plays and their effect on your taste are removed.");
    await capture(page, "history-delete", width, theme);
    await deletion.getByRole("button", { name: "Cancel", exact: true }).click();
    expect(state.deletes).toEqual([`?revision=${preview.revision}`]);
  });
}

function batchPreviews(state = 0): HistoryImport[] {
  return batchFiles.map((entry, index) => ({ ...preview, importId: batchIds[index], displayFileName: entry.name, state,
    preview: { ...preview.preview, earliest: batchDates[index][0], latest: batchDates[index][1] } }));
}

for (const width of [390, 1280]) for (const theme of ["light", "dark"] as const) {
  test(`History batch upload, preview, progress and group at ${width}px ${theme}`, async ({ page }) => {
    await appearance(page, width, theme);
    const state = await mockHistory(page);
    const releases: (() => void)[] = [];
    for (const item of batchPreviews()) {
      const gate = new Promise<void>(resolve => { releases.push(resolve); });
      state.responses[item.displayFileName] = { gate, item };
    }
    await openHistory(page);
    const dialog = await upload(page, batchFiles);
    const card = page.getByRole("region", { name: "Listening history from other services", exact: true });
    await expect.poll(() => state.uploads.length).toBe(1);
    await expect(card).toContainText("File 1 of 3");
    expect(state.activeUploads).toBe(1);
    releases[0]();
    await expect.poll(() => state.uploads.length).toBe(2);
    await expect(card).toContainText("File 2 of 3");
    expect(state.activeUploads).toBe(1);
    releases[1]();
    await expect.poll(() => state.uploads.length).toBe(3);
    await expect(card).toContainText("File 3 of 3");
    await expect(card).toContainText(/\d+%/);
    await expect(page.getByRole("progressbar", { name: "History upload", exact: true })).toBeVisible();
    expect(state.maxActiveUploads).toBe(1);
    expect(state.uploads.map(entry => new URLSearchParams(entry.query).get("fileName"))).toEqual(batchFiles.map(entry => entry.name));
    await capture(page, "history-batch-upload", width, theme);
    releases[2]();
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText("144,630 plays");
    await expect(dialog).toContainText("4,500 skips");
    await expect(dialog).toContainText("9,630 songs");
    await expect(dialog).toContainText("12 rows not recognized");
    await expect(dialog).toContainText("Jan 1, 2016 – Oct 28, 2026");
    for (const entry of batchFiles) await expect(dialog).toContainText(entry.name);
    await expect(dialog.getByText("Ready", { exact: true })).toHaveCount(3);
    for (const range of ["2016", "2019", "2020", "2023", "2024", "2026"]) await expect(dialog).toContainText(range);
    await expect(dialog.getByText("48,210 plays", { exact: false })).toHaveCount(3);
    await capture(page, "history-batch-preview", width, theme);
    expect(state.confirms).toEqual([]);
    await dialog.getByRole("button", { name: "Import all", exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect.poll(() => state.confirmIds).toEqual(batchIds);
    expect(state.confirms).toEqual(batchIds.map(() => ({ revision: preview.revision })));
    const savedBatches = await page.evaluate(key => JSON.parse(localStorage.getItem(key) ?? "{}") as Record<string, string>, batchKey);
    const groupId = savedBatches[batchIds[0]];
    expect(groupId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
    expect(batchIds.map(importId => savedBatches[importId])).toEqual(batchIds.map(() => groupId));
    state.items = batchPreviews(1).map((item, index) => ({ ...item, state: index === 0 ? 3 : index === 1 ? 2 : 1,
      importedRows: index === 0 ? 50000 : index === 1 ? 21000 : 0 }));
    await page.getByRole("button", { name: "Refresh imports", exact: true }).click();
    await expect(card).toContainText("File 2 of 3");
    await expect(card).toContainText(/47%/);
    await expect(card).toContainText("Spotify · 3 files · 2016–2026 · 144,630 plays");
    await expect(card.getByRole("button", { name: "Delete", exact: true })).toHaveCount(1);
    await expect(card.locator("summary").filter({ hasText: "Show files" })).toHaveCount(1);
    await capture(page, "history-batch-collapsed", width, theme);
    state.listLimit = 2;
    await page.reload();
    await expect(card).toContainText("Spotify · 3 files · 2016–2026 · 144,630 plays");
    await expect(card.getByRole("button", { name: "Delete", exact: true })).toHaveCount(1);
    await card.locator("summary").filter({ hasText: "Show files" }).click();
    for (const entry of batchFiles) await expect(card).toContainText(entry.name);
    await expect(card.locator("details")).toHaveAttribute("open", "");
    await capture(page, "history-batch-expanded", width, theme);
    await card.getByRole("button", { name: "Delete", exact: true }).click();
    const deletion = page.getByRole("alertdialog", { name: "Delete these imports?", exact: true });
    await expect(deletion).toBeVisible();
    expect(state.deleteIds).toEqual([]);
    await capture(page, "history-batch-delete", width, theme);
    await deletion.getByRole("button", { name: "Delete", exact: true }).click();
    await expect(deletion).toBeHidden();
    await expect(card).toContainText("No imports yet.");
    expect([...state.deleteIds].sort()).toEqual([...batchIds].sort());
    expect(state.deletes).toEqual(batchIds.map(() => `?revision=${preview.revision}`));
  });
}

test("Raw upload previews before confirmation, then polling updates progress and stats", async ({ page, baseURL }) => {
  await page.addInitScript(() => Object.defineProperty(crypto, "randomUUID", { value: undefined }));
  const state = await mockHistory(page);
  const origin = new URL(baseURL!).origin;
  const server = createServer(async (request, response) => {
    const headers = { "Access-Control-Allow-Origin": origin, "Access-Control-Allow-Credentials": "true",
      "Access-Control-Allow-Methods": "POST, OPTIONS", "Access-Control-Allow-Headers": "Content-Type" };
    if (request.method === "OPTIONS") { response.writeHead(204, headers); response.end(); return; }
    const chunks: Buffer[] = [];
    for await (const chunk of request) chunks.push(Buffer.from(chunk));
    state.uploads.push({ query: new URL(request.url!, origin).search, body: Buffer.concat(chunks),
      length: request.headers["content-length"], contentType: request.headers["content-type"] });
    state.items.unshift(structuredClone(preview));
    response.writeHead(200, { ...headers, "Content-Type": "application/json" });
    response.end(JSON.stringify(preview));
  });
  await new Promise<void>((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", resolve); });
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("The upload receiver did not start.");
  await page.route("**/api/admin/history/imports?fileName=history.zip", route =>
    route.continue({ url: `http://127.0.0.1:${address.port}${root}?fileName=history.zip` }));
  try {
    await openHistory(page);
    const dialog = await upload(page);
    await expect(dialog).toBeVisible();
    expect(state.uploads).toEqual([{ query: "?fileName=history.zip", body: file.buffer, length: String(file.buffer.length), contentType: "application/octet-stream" }]);
    expect(state.confirms).toEqual([]);
    await dialog.getByRole("button", { name: "Import", exact: true }).click();
    await expect(dialog).toBeHidden();
    expect(state.confirms).toEqual([{ revision: preview.revision }]);
    await expect(page.getByText("Waiting", { exact: true })).toBeVisible();
    state.progress = true;
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await expect(page.getByText("Importing 42%", { exact: true })).toBeVisible();
    const reads = state.statsReads;
    state.complete = true;
    await expect(page.getByText("Imported 48,210 plays from Spotify (2016–2026)", { exact: true })).toBeVisible();
    await expect.poll(() => state.statsReads).toBeGreaterThan(reads);
    await expect(page.getByText("Imported from other services: 48,210 plays", { exact: true })).toBeVisible();
    await expect(page.getByText("Done", { exact: true })).toBeVisible();
  } finally {
    server.closeAllConnections();
    await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  }
});

for (const failure of ["network", "size", "format", "code"] as const) test(`Upload ${failure} failure uses plain words and permits retry`, async ({ page }) => {
  const state = await mockHistory(page);
  state.networkFailure = failure === "network";
  state.uploadStatus = failure === "size" ? 413 : 400;
  state.uploadBody = failure === "format" ? { error: "history_import_format_unsupported", message: "The file does not match a supported listening-history format." } : { error: "history_import_failed" };
  await openHistory(page);
  await upload(page);
  await expect(page.getByRole("alert")).toContainText(failure === "size" ? "Choose a history file up to 500 MB." : failure === "format" ? "Not a history file" : "Your history could not be uploaded. Try again.");
  await expect(page.locator("body")).not.toContainText("history_import_");
  expect(state.confirms).toEqual([]);
  state.networkFailure = false; state.uploadStatus = 200;
  await expect(await upload(page)).toContainText("48,210 plays");
});

test("Mixed selection keeps successful previews while failed and non-history files explain their status", async ({ page }) => {
  const state = await mockHistory(page);
  state.responses["broken.json"] = { status: 400, body: { error: "history_import_failed", message: "This file could not be read." } };
  state.responses["settings.json"] = { status: 400, body: { error: "history_import_format_unsupported", message: "The file does not match a supported listening-history format." } };
  await openHistory(page);
  const dialog = await upload(page, [batchFiles[0], { ...batchFiles[0], name: "broken.json" }, { ...batchFiles[0], name: "notes.txt", mimeType: "text/plain" }, { ...batchFiles[0], name: "settings.json" }, batchFiles[2]]);
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText("96,420 plays");
  await expect(dialog).toContainText("3,000 skips");
  await expect(dialog).toContainText("6,420 songs");
  await expect(dialog).toContainText("8 rows not recognized");
  await expect(dialog).toContainText("broken.json");
  await expect(dialog).toContainText("This file could not be read.");
  await expect(dialog).toContainText("notes.txt");
  await expect(dialog).toContainText("settings.json");
  await expect(dialog.getByText("Skipped · Not a history file", { exact: true })).toHaveCount(2);
  expect(state.uploads.map(entry => new URLSearchParams(entry.query).get("fileName"))).toEqual([batchFiles[0].name, "broken.json", "settings.json", batchFiles[2].name]);
  expect(state.maxActiveUploads).toBe(1);
  const successIds = state.items.map(item => item.importId).sort();
  await dialog.getByRole("button", { name: "Import all", exact: true }).click();
  await expect(dialog).toBeHidden();
  expect([...state.confirmIds].sort()).toEqual(successIds);
  expect(state.deleteIds).toEqual([]);
});

for (const dismissal of ["Cancel", "Escape"] as const) test(`${dismissal} removes every new preview without importing`, async ({ page }) => {
  const existing = { ...preview, importId: "88888888-8888-4888-8888-888888888888", state: 3 };
  const state = await mockHistory(page, [existing]);
  await openHistory(page);
  const dialog = await upload(page, batchFiles);
  await expect(dialog).toContainText("144,630 plays");
  await expect(dialog.getByRole("button", { name: "Cancel", exact: true })).toHaveAttribute("type", "button");
  if (dismissal === "Cancel") await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  else await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect.poll(() => [...state.deleteIds].sort()).toEqual([...batchIds].sort());
  expect(state.deletes).toEqual(batchIds.map(() => `?revision=${preview.revision}`));
  expect(state.confirms).toEqual([]);
  expect(state.items.map(item => item.importId)).toEqual([existing.importId]);
});

test("Failed preview cleanup stays open so Cancel can retry every remaining file", async ({ page }) => {
  const state = await mockHistory(page);
  state.deleteFailure = true;
  await openHistory(page);
  const dialog = await upload(page, batchFiles);
  await expect(dialog).toContainText("144,630 plays");
  await page.keyboard.press("Escape");
  await expect(dialog.getByRole("alert")).toContainText("The import changed. Refresh and try again.");
  await expect(dialog).toBeVisible();
  await expect.poll(() => state.deleteIds.length).toBe(3);
  expect(state.confirms).toEqual([]);
  state.deleteFailure = false;
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(state.items).toEqual([]);
  expect(state.confirms).toEqual([]);
});

test("A saved preview batch has one review action and cancelling a review keeps its files", async ({ page }) => {
  const state = await mockHistory(page, batchPreviews());
  await page.addInitScript(({ key, ids }) => localStorage.setItem(key, JSON.stringify(Object.fromEntries(ids.map(importId => [importId, "99999999-9999-4999-8999-999999999999"])))), { key: batchKey, ids: batchIds });
  await openHistory(page);
  const card = page.getByRole("region", { name: "Listening history from other services", exact: true });
  await expect(card.getByRole("button", { name: "Review", exact: true })).toHaveCount(1);
  await expect(card.getByRole("button", { name: "Delete", exact: true })).toHaveCount(1);
  await card.getByRole("button", { name: "Review", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Import history", exact: true });
  await expect(dialog).toContainText("144,630 plays");
  await expect(dialog.getByRole("button", { name: "Import all", exact: true })).toBeVisible();
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(state.confirms).toEqual([]);
  expect(state.deleteIds).toEqual([]);
  expect(state.items).toHaveLength(3);
});

test("Malformed saved grouping does not prevent loading history", async ({ page }) => {
  await mockHistory(page, [{ ...preview, state: 3, importedRows: 50000 }]);
  await page.addInitScript(key => localStorage.setItem(key, "{"), batchKey);
  await openHistory(page);
  await expect(page.getByRole("button", { name: "Delete", exact: true })).toHaveCount(1);
  await expect(page.getByText("Done", { exact: true })).toBeVisible();
});

test("Preview confirmation failure remains in the dialog in words", async ({ page }) => {
  const state = await mockHistory(page);
  state.confirmFailure = true;
  await openHistory(page);
  const dialog = await upload(page);
  await dialog.getByRole("button", { name: "Import", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("The preview has expired. Choose the file again.");
  await expect(dialog).toBeVisible();
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  expect(state.confirms).toHaveLength(1);
});

test("Delete uses the current revision and refreshes stats after confirmation", async ({ page }) => {
  const state = await mockHistory(page, [{ ...preview, state: 3, importedRows: 50000 }]);
  await openHistory(page);
  await expect(page.getByText("Imported from other services: 48,210 plays", { exact: true })).toBeVisible();
  const reads = state.statsReads;
  await page.getByRole("button", { name: "Delete", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Delete this import?", exact: true });
  expect(state.deletes).toEqual([]);
  state.deleteFailure = true;
  await dialog.getByRole("button", { name: "Delete", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("The import changed. Refresh and try again.");
  state.deleteFailure = false;
  await dialog.getByRole("button", { name: "Delete", exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByText("No imports yet.", { exact: true })).toBeVisible();
  expect(state.deletes).toEqual([`?revision=${preview.revision}`, `?revision=${preview.revision}`]);
  await expect.poll(() => state.statsReads).toBeGreaterThan(reads);
  await expect(page.getByText("Imported from other services: 0 plays", { exact: true })).toBeVisible();
});

test("Background refresh keeps the preview open, list visible and refresh button still", async ({ page }) => {
  const state = await mockHistory(page, [{ ...preview, importId: "88888888-8888-4888-8888-888888888888", state: 2, importedRows: 21000 }]);
  await openHistory(page);
  const dialog = await upload(page);
  await expect(dialog).toBeVisible();
  const reads = state.reads;
  state.pollFailure = true;
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect.poll(() => state.reads).toBeGreaterThan(reads);
  await expect(dialog).toBeVisible();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Refresh imports", exact: true })).not.toHaveAttribute("aria-busy", "true");
  await expect(page.getByText("Importing 42%", { exact: true })).toBeVisible();
});
