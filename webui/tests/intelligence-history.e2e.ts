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
const stats: ListeningStats = {
  period: { from: null, to: "2026-10-28T12:00:00Z", timeZoneId: "UTC" }, playCount: 24, activeDays: 2, importedCount: 0,
  observedSince: null, estimatedTimeFromSongLengthsMilliseconds: 3600000, coverage: { unknownSongIdentityCount: 0, unknownDurationCount: 0 }, topSongs: [], topArtists: [], topAlbums: [],
};

async function mockHistory(page: Page, items: HistoryImport[] = []) {
  await mockIntelligence(page, { stats });
  const state = { items: structuredClone(items), confirms: [] as unknown[], deletes: [] as string[], uploads: [] as { query: string; body: Buffer | null; length: string | undefined; contentType: string | undefined }[],
    reads: 0, statsReads: 0, uploadStatus: 200, uploadBody: {} as unknown, uploadGate: null as Promise<void> | null, networkFailure: false, progress: false, complete: false, pollFailure: false, deleteFailure: false, confirmFailure: false };
  await page.route("**/api/admin/stats?*", route => { state.statsReads++; return route.fulfill({ json: { ...stats, importedCount: state.items.filter(item => item.state === 3).reduce((count, item) => count + item.preview.completed, 0) } }); });
  await page.route("**/api/admin/history/imports**", async route => {
    const request = route.request(), url = new URL(request.url());
    if (!["localhost", "127.0.0.1"].includes(url.hostname)) return route.abort();
    if (url.pathname === root && request.method() === "POST") {
      const headers = await request.allHeaders();
      state.uploads.push({ query: url.search, body: request.postDataBuffer(), length: headers["content-length"], contentType: headers["content-type"] });
      await state.uploadGate;
      if (state.networkFailure) return route.abort();
      if (state.uploadStatus !== 200) return route.fulfill({ status: state.uploadStatus, json: state.uploadBody });
      state.items.unshift(structuredClone(preview));
      return route.fulfill({ json: preview });
    }
    if (url.pathname === root) return route.fulfill({ json: state.items });
    if (request.method() === "DELETE") {
      expect(url.pathname).toBe(`${root}/${id}`);
      state.deletes.push(url.search);
      if (state.deleteFailure) return route.fulfill({ status: 409, json: { error: "history_import_conflict", message: "The import changed. Refresh and try again." } });
      state.items = state.items.filter(item => !url.pathname.endsWith(item.importId));
      return route.fulfill({ json: { removedListens: 50000 } });
    }
    if (url.pathname === `${root}/${id}/confirm` && request.method() === "POST") {
      state.confirms.push(request.postDataJSON());
      if (state.confirmFailure) return route.fulfill({ status: 409, json: { error: "history_import_conflict", message: "The preview has expired. Choose the file again." } });
      state.items[0] = { ...state.items[0], state: 1, jobId: "job", jobState: "pending" };
      return route.fulfill({ json: state.items[0] });
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
async function upload(page: Page) {
  const chooser = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Import history", exact: true }).click();
  await (await chooser).setFiles(file);
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
    await card.getByRole("button", { name: "Delete", exact: true }).first().click();
    const deletion = page.getByRole("alertdialog", { name: "Delete this import?", exact: true });
    await expect(deletion).toContainText("Its plays and their effect on your taste are removed.");
    await capture(page, "history-delete", width, theme);
    await deletion.getByRole("button", { name: "Cancel", exact: true }).click();
    expect(state.deletes).toEqual([]);
  });
}

test("Raw upload previews before confirmation, then polling updates progress and stats", async ({ page, baseURL }) => {
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
    await expect(page.getByText("Imported from other services: 48,210 plays (not in these charts)", { exact: true })).toBeVisible();
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
  await expect(page.getByRole("alert")).toContainText(failure === "size" ? "Choose a history file up to 500 MB." : failure === "format" ? "The file does not match a supported listening-history format." : "Your history could not be uploaded. Try again.");
  await expect(page.locator("body")).not.toContainText("history_import_");
  expect(state.confirms).toEqual([]);
  state.networkFailure = false; state.uploadStatus = 200;
  await expect(await upload(page)).toBeVisible();
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
  await expect(page.getByText("Imported from other services: 48,210 plays (not in these charts)", { exact: true })).toBeVisible();
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
  await expect(page.getByText("Imported from other services: 0 plays (not in these charts)", { exact: true })).toBeVisible();
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
