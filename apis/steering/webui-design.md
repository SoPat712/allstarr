# Allstarr WebUI Design Contract

## Product direction

Allstarr is a dense music control surface, not a generic administration form. Every
screen must make the primary action obvious, preserve provider and artwork context,
and progressively disclose technical identifiers.

## Shared layout

- Use `view-stack`, `view-header`, `section-heading`, `panel`, and `card`.
- Page tabs use the shared rounded `subnav` treatment. Do not create page-specific
  tab components.
- Desktop content is bounded by `--page-max`; mobile content uses one column and no
  horizontal document overflow.
- Controls use `--control-height`, `--control-font-size`, and spacing tokens.
- Settings disclosures use compact closed summaries and only expand to fit content.

## Navigation

- Primary rail: Home, Library, Sources, Event log, Settings.
- Library tabs: Playlists, Mappings, Cached, Kept.
- Settings tabs: General, Accounts, Provider routing, Extensions, Maintenance.
- Tabs remain deep-linkable, keyboard navigable, and horizontally scrollable on
  narrow screens.

## Dialogs

- Every modal uses a fixed viewport backdrop and a dialog surface rendered above
  route content.
- Dialogs trap focus, close on Escape, restore focus, lock background scrolling,
  and reset when the route changes.
- Mobile dialogs occupy the viewport and keep the title and primary action visible.
- Nested details use a dialog stack; they must never render at document-flow offsets.

## Music identity

- Prefer album or playlist artwork. Fall back to a provider icon, then a neutral
  music glyph.
- Source and target identities are presented as `source -> target` with provider
  logos, title, artist, album, outcome, and confidence.
- ISRCs, provider IDs, backend IDs, correlation IDs, route provenance, quality,
  cache age, and raw failures live in expandable technical details.

## States and feedback

- Loading states use stable skeletons and never flash false zero/unknown metrics.
- Empty states explain the next useful action.
- Success, warning, failure, and review states use consistent semantic pills.
- Destructive operations require explicit confirmation and describe retained data.
- Connectivity uses four bars, a textual state, and exact measured latency in a
  tooltip or details region.

## Responsive and accessibility requirements

- Test 390x844, 768x1024, 1280x800, and 1440x900.
- No clipped buttons, stale desktop sidebar overlays, or nested horizontal scrollers.
- Preserve visible focus, meaningful labels, reduced motion, contrast, touch targets,
  and screen-reader status announcements.
- Tables become semantic card rows on mobile without losing labels or actions.

## Engineering rule

Add or improve a shared primitive before adding a page-specific override. Repeated
markup, spacing, tabs, status pills, provider badges, dialog shells, metrics, and
empty states must be extracted rather than copied.
