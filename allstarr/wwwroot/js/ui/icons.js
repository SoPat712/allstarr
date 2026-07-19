import { html, nothing } from "/js/lit-3.3.3.js";

const paths = {
  home: html`<path d="m3 11 9-8 9 8"></path><path d="M5 10v10h14V10"></path><path d="M9 20v-6h6v6"></path>`,
  library: html`<path d="M4 4h5v16H4z"></path><path d="M9 6h5v14H9z"></path><path d="m14 5 4-1 3 15-4 1z"></path>`,
  sources: html`<circle cx="6" cy="6" r="2"></circle><circle cx="18" cy="6" r="2"></circle><circle cx="12" cy="18" r="2"></circle><path d="M8 7.5 11 16M16 7.5 13 16M8 6h8"></path>`,
  activity: html`<path d="M3 12h4l2-6 4 12 2-6h6"></path>`,
  settings: html`<circle cx="12" cy="12" r="3"></circle><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6v.2h-4V21a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14H2.8v-4H3a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6 1.7 1.7 0 0 0 10 3V2.8h4V3a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1h.2v4H21a1.7 1.7 0 0 0-1.6 1Z"></path>`,
  search: html`<circle cx="11" cy="11" r="7"></circle><path d="m20 20-4-4"></path>`,
  refresh: html`<path d="M20 6v5h-5"></path><path d="M4 18v-5h5"></path><path d="M18 9a7 7 0 0 0-12-3L4 8M6 15a7 7 0 0 0 12 3l2-2"></path>`,
  plus: html`<path d="M12 5v14M5 12h14"></path>`,
  shield: html`<path d="M12 3 4 6v5c0 5 3.4 8.5 8 10 4.6-1.5 8-5 8-10V6z"></path><path d="m9 12 2 2 4-4"></path>`,
  playlist: html`<path d="M4 6h10M4 11h10M4 16h7"></path><path d="M18 5v11a2 2 0 1 1-2-2h2"></path>`,
  tasks: html`<rect x="3" y="3" width="7" height="7" rx="2"></rect><rect x="14" y="3" width="7" height="7" rx="2"></rect><rect x="3" y="14" width="7" height="7" rx="2"></rect><path d="m15 18 2 2 4-5"></path>`,
  server: html`<rect x="3" y="4" width="18" height="6" rx="2"></rect><rect x="3" y="14" width="18" height="6" rx="2"></rect><path d="M7 7h.01M7 17h.01"></path>`,
  clock: html`<circle cx="12" cy="12" r="9"></circle><path d="M12 7v5l3 2"></path>`,
  check: html`<path d="m5 12 4 4L19 6"></path>`,
  warning: html`<path d="M12 3 2.5 20h19z"></path><path d="M12 9v4M12 17h.01"></path>`,
  more: html`<circle cx="5" cy="12" r="1"></circle><circle cx="12" cy="12" r="1"></circle><circle cx="19" cy="12" r="1"></circle>`,
  filter: html`<path d="M4 5h16l-6 7v6l-4 2v-8z"></path>`,
  logout: html`<path d="M10 4H5v16h5M14 8l4 4-4 4M9 12h9"></path>`,
  user: html`<circle cx="12" cy="8" r="4"></circle><path d="M4 21a8 8 0 0 1 16 0"></path>`,
  close: html`<path d="m6 6 12 12M18 6 6 18"></path>`,
  chevronLeft: html`<path d="m15 18-6-6 6-6"></path>`,
  chevronRight: html`<path d="m9 18 6-6-6-6"></path>`,
};

export function icon(name, size = 18) {
  const body = paths[name];
  if (!body) return nothing;
  return html`<svg class="ui-icon" width=${size} height=${size} viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${body}</svg>`;
}
