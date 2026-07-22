# Allstarr WebUI engineering contract

Status: normative for the v3 WebUI

This document defines how the WebUI is implemented. The visual rules live in
[`../design/webui-design-system.md`](../design/webui-design-system.md); this file
owns component boundaries, state, data access, responsive rendering, and reuse.
When the two documents overlap, satisfy both. Update the shared primitive or
this contract instead of adding a page-specific exception.

## Required workflow

1. Identify the existing primitive that owns the interaction.
2. Add missing behavior to that primitive before using it on a page.
3. Keep remote state, view state, and form state separate.
4. Render every loading, empty, error, success, and permission-denied state.
5. Check desktop and mobile behavior before considering a screen complete.

New legacy CSS, inline style patches, manual DOM mutation, duplicated request
wrappers, and page-specific copies of shared controls are forbidden.

## Rendering architecture

- Keep the current Lit light-DOM architecture for v3. Shared CSS variables and
  cascade layers remain the styling boundary; do not introduce isolated Shadow
  DOM components during the beta stabilization cycle.
- A workspace component owns orchestration and remote state. Small shared
  components own one interaction, such as a tab rail, playlist picker, menu,
  dialog, connectivity meter, or paginated result list.
- Templates are pure projections of state. Do not query descendants, toggle
  `hidden`, set classes, or write values through `querySelector`. Change state
  and let Lit render the result.
- Use stable domain IDs as repeat keys. Array indexes are not valid keys for
  playlists, tracks, events, accounts, providers, or extensions.
- Event handlers call named methods for nontrivial work. Long anonymous handlers
  inside templates are not allowed.
- The workspace owns navigation and modal selection. A modal owns its draft and
  emits a typed completion or cancellation event.

## State ownership

Keep state in three explicit categories:

- **Remote state:** API records, cursors, revisions, loading state, and errors.
- **View state:** selected tab, open dialog, search query, filters, expanded row,
  and current wizard step.
- **Draft state:** unsaved form values and validation errors.

Do not mutate API objects in place. Create a draft or replace the remote record
after a successful response. Clear stale errors when a new request begins, but
do not erase the last successful result while a background refresh is running.

Every asynchronous operation must:

- enter a visible busy state immediately;
- disable only controls that would conflict with the operation;
- support cancellation when superseded or when its dialog closes;
- ignore stale responses;
- normalize expected errors into readable inline feedback;
- leave unexpected technical details available only in an expandable section.

## API access

- All requests go through the shared API client. Components never call `fetch`
  directly or create their own authentication, JSON, or error wrappers.
- Query builders omit empty values and encode every user-controlled value.
- Search requests debounce input and cancel the previous request.
- Cursor pagination is preferred for providers, playlists, tracks, events, and
  extensions. Do not infer a page count when the upstream API exposes cursors.
- Mutation responses return the canonical updated record and revision. Replace
  local state from that response rather than guessing the resulting state.
- Secrets, provider tokens, authenticated artwork URLs, and backend credentials
  never enter client state or browser-visible URLs.
- Artwork that requires authorization is served through an Allstarr proxy with
  cache metadata and a safe fallback.

## Shared primitives

The following concepts have one implementation each:

| Primitive | Required behavior |
| --- | --- |
| Workspace tab rail | Rounded container, active underline/fill from the design contract, keyboard navigation, horizontal mobile scroll |
| Page header | Identity and description on the left, one primary action on the right |
| Form control | Shared typography, dimensions, inset, states, validation, and touch behavior for its control type |
| Data surface | Semantic desktop table and mobile cards from one normalized row model |
| Playlist picker | Search, cursor pagination, artwork, provider identity, track count, loading/error/empty states, ID/URL fallback |
| Provider/account picker | Capability-filtered accounts, provider icon, account state, disabled reason |
| Stepper | Named steps, current/completed states, back/next controls, retained draft, error focus |
| Menu | Anchored popover, roving keyboard focus, Escape dismissal, focus restoration |
| Dialog | Focus trap, labelled title, internal scrolling, backdrop/Escape dismissal, focus restoration |
| Disclosure | Compact summary row; advanced content only, never primary navigation |
| Connectivity meter | Four bars, text alternative, exact timing tooltip, tested timestamp |
| Status chip | Shared state vocabulary and color semantics |
| Empty/error state | One explanation and one recovery action at most |

Pages may compose these primitives but may not redefine their spacing, states,
icons, or keyboard behavior.

### App shell navigation

- The primary navigation has exactly five destinations in this order: Home, Library, Sources, Event Log, and Settings.
- Primary destinations are direct links. They never live inside a disclosure,
  overflow menu, or route-local navigation group.
- Expanded desktop, collapsed rail, and mobile overlay use the same route model,
  icon, label, active state, focus state, and ordering.
- Additional workflows are reached from their owning workspace. Adding a sixth
  primary destination requires updating both WebUI contracts and the visual
  design contract.

### Form-control ownership

- Every native input, select, textarea, checkbox, radio, and button inherits its
  metrics from the shared control tokens. Page grids may change width, not font,
  height, inset, border, radius, or interaction states.
- The normal control is `40px` high, uses the body control font at `14px`, and
  has `12px` horizontal inset. Compact and primary-action sizes must be explicit
  named variants; they cannot arise from selector specificity or browser defaults.
- Labels, ownership badges, helper text, validation, and controls align to one
  field rhythm. Optional metadata may wrap but cannot change the paired control's
  dimensions or baseline.
- Native controls must be verified with long values, browser zoom, coarse input,
  dark/light/system themes, and every documented responsive width.

### Compact disclosure ownership

- The shared disclosure primitive owns border, radius, marker, focus, and open
  state. A workspace may select the compact variant but may not restyle those
  pieces independently.
- Settings renders closed groups as compact summary rows, not feature cards.
  The summary uses the compact control rhythm and must not acquire a card
  `min-height`, page-section padding, or shadow.
- Expanded content receives its own inset below a divider. Opening a disclosure
  must not change the summary's width, margin, or horizontal alignment.
- Component rules live in the workspace layer and breakpoint adjustments live
  only in the responsive layer. The legacy/base layer must not contain a second
  settings-specific override.
- Validate the computed summary height and padding at desktop and mobile sizes;
  checking only the authored selector is insufficient when cascade layers are
  involved.

## Forms and guided workflows

- Use a single-page form for short tasks with at most five related fields.
- Use a stepper when choices alter later data or when the user needs to select
  two remote resources. Playlist linking uses `Source -> Target -> Behavior ->
  Review`.
- Only render fields relevant to the selected provider, protocol, capability,
  and mode. Hidden fields are not submitted.
- Account credentials are managed under Sources. Workflow forms reference an
  account ID and never ask for the same credential again.
- Derive tenant, owner, backend instance, principal, library scope, provider ID,
  and other internal identifiers from authenticated account records whenever
  possible. Put unavoidable raw identifiers in a collapsed Advanced section.
- Validate the current step before advancing. Move focus to the first invalid
  field and show a field-level message plus a concise step summary.
- Review steps use server previews for destructive or high-impact operations.
  Client-side estimates must be labelled as estimates.
- Preserve a draft while moving backward. Cancel discards it without creating
  records, credentials, schedules, or background jobs.

## Playlist identity and artwork

- Source and target playlists use the same normalized summary model: stable
  reference, provider/account, name, description, owner, track count, artwork,
  modification time, and writable state.
- Render playlist artwork when supplied. Fall back to a provider/package icon,
  then generated initials. Never show an empty image frame.
- Provider/package identity remains visible beside artwork so similarly named
  playlists from different services are distinguishable.
- Authenticated upstream artwork is proxied. The browser must not receive an
  upstream authorization header, token-bearing URL, or private server address.
- Image dimensions are reserved before loading to prevent row movement.

## Repeated data and responsive behavior

- Desktop repeated data uses a semantic table with one header surface and one
  action column. Mobile uses cards generated from the same row model.
- Do not force a desktop table into a viewport where its primary identity and
  action become inaccessible. Switch presentation at the documented breakpoint.
- Primary and overflow actions stay in one horizontal action group.
- Use virtualization for track or event collections that can exceed 100 rows.
  The DOM must not contain the full remote collection.
- Preserve search, filters, selection, and scroll context after row actions.
- Empty results replace the body; they do not leave a meaningless header and
  paginator behind.

## Styling rules

- Use only design tokens and the declared CSS cascade order. New component
  rules belong in primitives or workspaces; responsive overrides belong in the
  final responsive layer.
- Spacing uses `8, 12, 16, 20, 24, 32, 40px`. Arbitrary one-off spacing requires
  a documented token addition.
- Use shared card, control, modal, and tab radii. A page must not locally remove
  or restore rounding.
- Reserve shadows for floating UI. Selection uses the accent border/state, not
  a new background color invented by a page.
- Truncation must retain the full accessible name and provide a tooltip when the
  hidden value is useful.

## Accessibility and interaction

- Every interactive element is reachable by keyboard and has a visible focus
  state. Do not attach click behavior to a noninteractive `div`.
- Icon-only buttons require an accessible name. Decorative icons are hidden
  from assistive technology.
- Async status uses an appropriate live region without announcing every polling
  refresh.
- Color is never the sole status signal. Connectivity bars expose their score,
  exact timing, metric type, and tested time as text.
- Respect reduced motion. Do not animate layout, long lists, or modal contents.
- Restore focus to the opener after dialogs and menus close.

## Performance rules

- Debounce remote search and cancel superseded requests.
- Coalesce identical in-flight reads at the API layer.
- Pause nonessential polling while the document is hidden.
- Avoid filtering or mapping the same large collection repeatedly during one
  render; derive it once from stable inputs.
- Lazy-load dialog-only data when the dialog opens.
- Use image sizing, lazy loading, and proxied cache headers for artwork grids.
- Keep provider concurrency bounded by backend policy; the UI must not fan out
  one request per visible row.

## Definition of done

A WebUI change is complete only when all applicable answers are yes:

- Does it use an existing shared primitive or add one centrally?
- Are remote, view, and draft state separate?
- Are irrelevant fields absent rather than merely disabled or hidden manually?
- Are loading, empty, error, permission, success, and stale-response states handled?
- Are network requests cancellable and deduplicated where appropriate?
- Does repeated data work as a table on desktop and cards on mobile?
- Are artwork and provider identity shown with documented fallbacks?
- Do keyboard navigation, focus restoration, and screen-reader labels work?
- Does Redact mode remove identifying content from UI and diagnostics?
- Does the screen follow the visual design contract at 1180, 900, 760, and 620px?
- Are changed public behaviors covered by focused tests?
- Were steering docs updated if a shared convention changed?
