# Allstarr WebUI design contract

Status: normative for the v3 WebUI
Reference set: the supplied Home, Sources, Injected playlists, playlist-details,
Extensions, extension-install, permission-review, extension-management, and
now-playing images.

This document is the visual and interaction contract for the WebUI. Reference
images define the intended information hierarchy; this document turns that
hierarchy into reusable rules. New screens must use these primitives instead of
introducing page-specific spacing, card, button, status, or modal systems.
Implementation architecture, Lit state ownership, API access, responsive data
rendering, and the definition of done are normative in
[`../steering/webui-engineering.md`](../steering/webui-engineering.md).

## Product principles

1. **One obvious place for every task.** Sources describes available services.
   Settings owns accounts and application preferences. Extensions has its own
   Settings workspace. Library owns playlist workflows. Activity owns history.
2. **Progressive disclosure without hidden destinations.** A primary workspace
   is never buried inside a collapsed panel. Modals contain focused create,
   review, and manage tasks. Disclosure rows are reserved for advanced details.
3. **State before controls.** Every card or row leads with identity and health,
   then capabilities or metrics, then one primary action and an overflow menu.
4. **Dense, not cramped.** Desktop screens should show meaningful rows without
   turning every data point into another nested card. Use dividers and tables for
   repeated data; reserve cards for distinct concepts.
5. **Provider-agnostic behavior.** Provider identity, capability, health, setup,
   and authorization are rendered from data. No page may hard-code Spotify as
   the generic playlist or refresh provider.
6. **Visible system feedback.** Long actions show an operation bubble with name,
   progress, and outcome. Buttons enter a busy state immediately and stay
   disabled until the operation finishes.

## Shell and page geometry

- Desktop sidebar: `244–248px`, full viewport height, one-pixel divider.
- Top bar: `72px`, with workspace title on the left, global search centered, and
  theme/refresh actions on the right.
- Main content: centered, maximum `1280px`, `32px` horizontal gutters and `28px`
  top spacing on desktop. The content column scrolls; the sidebar and top bar do
  not.
- Page stack gap: `20px`; section gap: `14px`; compact row gap: `10px`.
- At `<=1180px`, two-column content grids may collapse to one column, but rows
  must remain compact. At `<=760px`, the sidebar becomes an overlay and a click
  outside closes it.
- The now-playing surface occupies the shell's final grid row. It never overlays
  content and never creates a second competing bottom margin.

## Tokens

- Styles load through `css/app.css` in one declared cascade order:
  `tokens → legacy → foundation → primitives → shell → workspaces → responsive`.
  The legacy layer is transitional and may not receive new component rules.
  Breakpoint rules belong only in the final responsive layer.
- Sora is bundled locally for brand and page titles. Inter is bundled locally
  for controls and body copy. IDs, endpoints, and durations use the system
  monospace stack. Runtime font requests are forbidden.
- The core dark palette is Night deck `#0B0D12`, Rail `#10131A`, Console
  `#151922`, Raised surface `#1C222D`, Signal violet `#7C8CFF`, and Meter mint
  `#58C894`. Light-mode values are tokenized equivalents.
- Signal violet is the product signature. Use it for the active navigation path,
  playback progress, live operation progress, focus, and the primary action—not
  as decorative glow on ordinary surfaces.

- Base spacing unit: `4px`; use `8, 12, 16, 20, 24, 32, 40` only.
- Control height: `40px`; compact control: `34px`; primary CTA: `44px`.
- Card radius: `14px`; control radius: `9–10px`; modal radius: `16px`.
- Borders are low-contrast and one pixel. Accent borders indicate selection,
  never ordinary grouping.
- Shadows appear only on floating UI: modals, popovers, the operation bubble,
  and the primary CTA hover state.
- Primary text is near-white; secondary text is cool gray; muted text is used
  only for metadata. Green means healthy/success, amber means attention, red
  means destructive/failure, and violet is the interactive accent.

## Typography

- Page title: `28–32px`, weight `700`, tight tracking.
- Section title: `18–20px`, weight `650–700`.
- Card/row title: `15–16px`, weight `650`.
- Body: `14px`; metadata: `12px`; eyebrow/table heading: `11px`, uppercase,
  letter-spaced.
- Labels and values must be separated by layout or weight, never concatenated
  (`SoundCloudVersion 1.0.5` is invalid; `SoundCloud` + `v1.0.5` is valid).

## Icons and provider branding

- Navigation and action buttons use the bundled Lucide-style SVG sprite at
  `18px`. Components call the shared `icon()` renderer; pages must not repeat
  inline SVG paths or fetch interface icons at runtime.
  Empty icon slots are forbidden: if an icon is unknown, render no slot.
- Provider marks use the package icon when supplied. Allstarr must resolve icons
  from manifest paths and conventional package files (`icon.png`, `.jpg`,
  `.jpeg`, `.webp`). Built-in providers alone use the local provider asset
  registry. A package icon always takes precedence over a built-in mapping.
- Provider containers: `48px` in cards, `44px` in overview metrics, `38px` in
  rows, `26px` in compact tables. Art uses `object-fit: contain`; user avatars
  and album/playlist art use `object-fit: cover`.
- Initials are the final fallback only. They must not replace an available
  package icon.

## Reusable primitives

### Page header

Identity block on the left (optional icon, title, one-sentence description) and
one primary action on the right. Never stack several unrelated buttons there.

### Tabs

Tabs sit immediately below the page header. Active state uses a violet bottom
border or filled compact pill, not both. Tabs do not live inside another card.

### Cards and rows

- A card represents one provider, one readiness concept, or one settings group.
- A row represents an extension, playlist, track, event, or repeated setting.
- Do not nest a full card inside another full card. Use an inset panel only for
  a cohesive metric strip, warning, or form section.
- Repeated rows share a header and dividers rather than independent outlines.

### Disclosures

- Disclosures expose advanced configuration inside an existing workspace; they
  are not navigation cards or page sections.
- Closed settings disclosures use a compact row with `8px 12px` inset, no
  shadow, and no inherited card minimum height. Adjacent rows use the compact
  row gap.
- Title and helper text form one tight identity block. The expand marker stays
  aligned at the far edge and does not reserve a second action column.
- Expanded content begins below a divider with `12px` inset. Its controls use
  the normal form rhythm; do not increase the closed summary to match the open
  body.
- Desktop and mobile use the same compact density. Mobile may wrap helper text,
  but must not add vertical card padding.

### Status

Use short chips: `Healthy`, `Connected`, `Degraded`, `Needs setup`, `Disabled`,
`Not checked`. “Available but untested” is not a status. Untested is displayed as
`Not checked` and the item remains clearly enabled or disabled.

### Buttons

One primary action per visual region. Secondary actions are neutral. Destructive
actions are red and isolated in a danger zone or overflow menu. Icon-only buttons
must have accessible labels and visible icons.

### Tables

Tables use one containing surface, a tinted header row, `56–64px` data rows,
aligned numeric columns, and horizontal scrolling below desktop widths. Row
actions remain at the far right. Empty states replace the body without retaining
meaningless controls.

### Modals

- Backdrop dims and blurs the page. Clicking the backdrop or pressing Escape
  closes the modal; clicking inside does not.
- Header contains identity, version/status, and one close button.
- Install and permission dialogs: `680–820px` wide.
- Manage and playlist-detail dialogs: `960–1040px` wide with a two-column body.
- Modal body scrolls internally; the page behind it does not move.
- Forms, permissions, support, activity, and danger actions stay inside the
  relevant modal rather than appearing farther down the Settings page.

## Page contracts

### Home

- Four equal overview cards: backend, current playlist-refresh provider,
  injected playlists, and active tasks. Provider identity is data-driven.
- Readiness is one full-width panel with a clear empty/result state.
- Setup and provider health form a balanced two-column row.
- Activity is a compact table with provider icons and event outcomes.
- Library overview is a compact playlist table with artwork and match status.

### Sources

- Contains service/provider definitions only; connected account credentials live
  in Settings.
- Desktop uses a responsive provider grid with a `520px` minimum card width;
  wide screens show two columns and narrower content shows one. Each card has provider identity,
  health, capability tags, a four-cell metric strip, account summary, optional
  warning, one `Manage` action, and an overflow menu.
- Music providers and metadata/helpers are separate labeled sections.
- `Add source` opens the catalog modal; `Manage accounts` navigates to Settings.

### Library / Injected

- Library tabs are directly below the workspace header.
- Injected playlists use a toolbar and one data table, not a stack of action
  buttons per row.
- Playlist artwork comes from the playlist itself; first-track artwork is only a
  last-resort fallback and must be identified as such by the API.
- Clicking a playlist opens the playlist-detail modal with playlist artwork,
  provider, playable count, destination, searchable tracks, and provider badges.
  Every track is rendered in one internally scrollable list; do not add
  artificial ten-row pagination.

### Library / Kept

- Kept downloads use one surface: Files/Size stat strip and actions in its
  header, with the file table directly below.
- Hide destructive bulk actions when no files exist. The empty state says where
  future kept tracks will appear and does not create a second empty card.

### Settings

- Default Settings is account and application configuration.
- Accounts are compact cards or rows with connection state and a manage action.
- Extensions is a dedicated Settings workspace reachable directly; it is never
  a collapsed disclosure below accounts.

### Extensions workspace

- Header: extension icon, `Extensions`, concise description, `Install extension`
  CTA.
- Tabs: `Installed`, `Available`, `Registries`, `Activity`.
- Installed tab: active registry summary, compact extension rows, capability
  legend, and recent extension activity. Each row shows real icon, name, author,
  version, short description, capability chips, state, and `Manage`.
- Available tab: catalog search and installable packages; installed packages are
  not duplicated.
- Registries tab: registry sources and refresh state.
- Activity tab: extension-only audit timeline.
- `Install extension` opens an in-place modal for registry, direct URL, or upload.
- Permission review and extension management are modal workflows.
- Provider settings and signed-session authorization live in the extension's
  management modal.
- Session grant input accepts either the raw grant or the full callback URI and
  extracts the `grant` query parameter. Invalid or mismatched callbacks produce a
  specific inline error.

### Now playing

- Three zones: track/artwork, progress, provider/scrobble state.
- Progress displays elapsed and duration, fills proportionally, and updates
  smoothly without resetting on ordinary polling jitter.
- Source provider and scrobble outcome are visible. Successful scrobbling shows
  a check; pending and failed states use distinct chips.

## Visual acceptance checklist

At desktop reference width:

- No duplicated names, concatenated labels, initials where an icon exists, empty
  icon placeholders, nested full cards, or primary workspace inside a disclosure.
- Page title, tabs, content edges, table columns, and modal edges align to the
  shared grid.
- Home, Sources, Library, Extensions, and Settings use the same header, spacing,
  status, button, and typography primitives.
- All actions provide immediate progress and a final success/error message.
- The UI remains usable at `1180px`, `900px`, `760px`, and `620px` breakpoints.
