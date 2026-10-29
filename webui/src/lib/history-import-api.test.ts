import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, historyImports } from "./api";
import { historyError } from "./history-import";

afterEach(() => vi.unstubAllGlobals());

function mockUpload() {
  const xhr = {
    open: vi.fn(), setRequestHeader: vi.fn(), send: vi.fn(), abort: vi.fn(),
    responseType: "", status: 200, statusText: "OK", response: {} as unknown,
    upload: { onprogress: null as ((event: { loaded: number }) => void) | null },
    onload: null as (() => void) | null, onerror: null as (() => void) | null,
    onabort: null as (() => void) | null, onloadend: null as (() => void) | null,
  };
  xhr.abort.mockImplementation(() => { xhr.onabort?.(); xhr.onloadend?.(); });
  vi.stubGlobal("XMLHttpRequest", class { constructor() { return xhr; } });
  return xhr;
}

describe("history upload transport", () => {
  it("sends the raw file through the mounted admin path, reports bytes and normalizes the preview", async () => {
    vi.stubGlobal("document", { querySelector: () => ({ getAttribute: () => "/control-room" }) });
    const xhr = mockUpload(), progress = vi.fn(), controller = new AbortController();
    const file = new File(["history"], "my history.zip");
    const result = historyImports.upload(file, progress, controller.signal);
    expect(xhr.open).toHaveBeenCalledWith("POST", "/control-room/api/admin/history/imports?fileName=my+history.zip");
    expect(xhr.send).toHaveBeenCalledWith(file);
    expect(xhr.setRequestHeader).toHaveBeenCalledExactlyOnceWith("Content-Type", "application/octet-stream");
    xhr.upload.onprogress?.({ loaded: 4 });
    expect(progress).toHaveBeenCalledWith(4);
    xhr.response = { ImportId: "import", State: 0, Preview: { Songs: 2, Completed: 3 } };
    xhr.onload?.(); xhr.onloadend?.();
    await expect(result).resolves.toEqual({ importId: "import", state: 0, preview: { songs: 2, completed: 3 } });
    controller.abort();
    expect(xhr.abort).not.toHaveBeenCalled();
  });

  it("keeps a safe preview explanation instead of displaying its error code", async () => {
    const xhr = mockUpload();
    const result = historyImports.upload(new File(["bad"], "history.json"), vi.fn(), new AbortController().signal).catch(error => error);
    xhr.status = 400;
    xhr.response = { Error: "history_import_format_unsupported", Message: "The file does not match a supported listening-history format." };
    xhr.onload?.(); xhr.onloadend?.();
    const error = await result;
    expect(error).toBeInstanceOf(ApiError);
    expect(historyError(error, "Try again.")).toBe("The file does not match a supported listening-history format.");
  });

  it("settles network failures and stops an upload when the view is left", async () => {
    const xhr = mockUpload(), controller = new AbortController();
    const result = historyImports.upload(new File(["history"], "history.json"), vi.fn(), controller.signal);
    const assertion = expect(result).rejects.toThrow("Upload cancelled");
    controller.abort();
    await assertion;
    expect(xhr.abort).toHaveBeenCalledOnce();
    const failed = historyImports.upload(new File(["history"], "history.json"), vi.fn(), new AbortController().signal);
    const failure = expect(failed).rejects.toThrow("Upload failed");
    xhr.onerror?.(); xhr.onloadend?.();
    await failure;
  });
});
