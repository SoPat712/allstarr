import { html, nothing } from "/js/lit-3.3.3.js";

const iconNames = new Set([
  "home", "library", "sources", "activity", "settings", "search", "refresh", "plus",
  "shield", "playlist", "tasks", "server", "clock", "check", "warning", "more",
  "filter", "logout", "user", "close", "chevronLeft", "chevronRight", "extensions",
  "metadata", "download", "streaming", "lyrics", "externalApi", "edit", "upload", "link",
  "lock", "pin",
]);

export function icon(name, size = 18) {
  if (!iconNames.has(name)) return nothing;
  return html`<svg class="ui-icon" width=${size} height=${size} viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><use href=${`/images/ui-icons.svg#${name}`}></use></svg>`;
}
