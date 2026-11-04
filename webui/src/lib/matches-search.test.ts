import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { installMatchSearchShortcut, takeMatchSearchRequest } from "./matches-search";

let surface: EventTarget & { location: { hash: string } };
let remove: () => void;
const querySelector = vi.fn();

function press(target: { tagName?: string; isContentEditable?: boolean; closest?: (selector: string) => unknown } = {}, modifiers = {}) {
  const event = Object.assign(new Event("keydown", { cancelable: true }), { key: "/", ...modifiers });
  Object.defineProperty(event, "target", { value: target });
  surface.dispatchEvent(event);
  return event;
}

beforeEach(() => {
  surface = Object.assign(new EventTarget(), { location: { hash: "#/library/matches" } });
  querySelector.mockReset().mockReturnValue(null);
  vi.stubGlobal("window", surface);
  vi.stubGlobal("document", { querySelector });
  remove = installMatchSearchShortcut();
});

afterEach(() => {
  remove();
  vi.unstubAllGlobals();
});

it("remembers slash before the search field mounts and consumes the request once", () => {
  const requested = vi.fn();
  surface.addEventListener("matches-search", requested);
  expect(press().defaultPrevented).toBe(true);
  expect(requested).toHaveBeenCalledOnce();
  expect(takeMatchSearchRequest()).toBe(true);
  expect(takeMatchSearchRequest()).toBe(false);
});

it.each(["INPUT", "TEXTAREA", "SELECT"])("leaves slash available for typing in %s", (tagName) => {
  expect(press({ tagName }).defaultPrevented).toBe(false);
  expect(takeMatchSearchRequest()).toBe(false);
});

it("requests search when a filter radio has focus", () => {
  expect(press({ closest: (selector: string) => selector.includes('[role="radiogroup"]') }).defaultPrevented).toBe(true);
  expect(takeMatchSearchRequest()).toBe(true);
});

it("leaves editable regions, modifiers, handled keys and open dialogs alone", () => {
  expect(press({ isContentEditable: true }).defaultPrevented).toBe(false);
  expect(press({ closest: () => true }).defaultPrevented).toBe(false);
  expect(press({}, { ctrlKey: true }).defaultPrevented).toBe(false);
  expect(press({}, { metaKey: true }).defaultPrevented).toBe(false);
  expect(press({}, { altKey: true }).defaultPrevented).toBe(false);
  const handled = Object.assign(new Event("keydown", { cancelable: true }), { key: "/" });
  handled.preventDefault();
  surface.dispatchEvent(handled);
  expect(takeMatchSearchRequest()).toBe(false);
  querySelector.mockReturnValue({});
  expect(press().defaultPrevented).toBe(false);
  expect(takeMatchSearchRequest()).toBe(false);
});

it("supports both Matches addresses and clears a pending request on navigation", () => {
  surface.location.hash = "#/library/mappings?search=Song";
  expect(press().defaultPrevented).toBe(true);
  surface.location.hash = "#/library/playlists";
  surface.dispatchEvent(new Event("hashchange"));
  expect(takeMatchSearchRequest()).toBe(false);
  expect(press().defaultPrevented).toBe(false);
  expect(takeMatchSearchRequest()).toBe(false);
});

it("removes the page listener and discards pending focus on cleanup", () => {
  press();
  remove();
  expect(takeMatchSearchRequest()).toBe(false);
  expect(press().defaultPrevented).toBe(false);
});

it("handles one request after the page mounts again", () => {
  const requested = vi.fn();
  surface.addEventListener("matches-search", requested);
  remove();
  expect(press().defaultPrevented).toBe(false);
  remove = installMatchSearchShortcut();
  expect(press().defaultPrevented).toBe(true);
  expect(requested).toHaveBeenCalledOnce();
  expect(takeMatchSearchRequest()).toBe(true);
});
