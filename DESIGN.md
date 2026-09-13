# Allstarr WebUI Design System

## Product stance

Allstarr is a music control room. Every route must quickly answer:

1. What is happening?
2. Which library, user, source, and account are involved?
3. Is it healthy and complete?
4. What can I safely do next?

## Visual system

Use Google Material 3 as the interaction and visual grammar, adapted to Allstarr rather than copied component by component.

- Role-based `--md-sys-color-*` tokens own light and dark themes.
- Allstarr blue is the only general action accent. Provider marks and music artwork provide the wider palette.
- Tonal surfaces communicate grouping. General panels do not use decorative glass, glow, gradients, or heavy shadows.
- Use one system sans family, restrained type sizes, sentence case, and short operational copy.
- Shapes follow Material roles: 8px fields, 12px compact containers, 16px panels, 24–28px dialogs, and full pills only for buttons, badges, and navigation indicators.
- Theme defaults to the operating-system preference and offers explicit System, Light, and Dark choices.

## Shared components

- Keep the existing Svelte, Bits UI, Tailwind, shadcn-style primitives, and Lucide stack. Do not add Material Web and duplicate the control layer.
- Routes compose shared Button, Checkbox, Select, Dialog, Menu, Tabs, Badge, Progress, Skeleton, Tooltip, and table patterns. They do not restyle raw controls.
- Provider identity uses the existing provider mark and artwork components.
- Creative canvas effects require a GPL-compatible implementation or license. Canvas UI is not vendored because its Commons Clause adds distribution restrictions that conflict with this repository's GPL terms.

## Geometry

- `--workspace-gutter` owns the outer content track, `--surface-gutter` owns panel insets, and `--section-gap` owns the vertical and horizontal rhythm between peer surfaces. Route CSS must reuse these tokens instead of introducing nearby fixed values.
- Route tabs span the same content track as the route header and primary surface. At compact widths they scroll horizontally with 44px minimum targets instead of shrinking labels or overflowing the viewport.
- Panel headings use two tracks: flexible title and context on the left, actions or status on the right. Below 650px, actions move to a full-width grid beneath the copy; an unpaired final action spans the grid.
- Peer dashboard cards use equal columns unless the task hierarchy documents a deliberate primary surface. A single grid item expands to the available track instead of retaining an empty half-column.
- Regular controls use the 44px `--control-md` baseline. Labels, controls, helper text, and footer actions align within a shared field grid.
- The compact bottom navigation reserves `--mobile-nav-height` on the scrolling workspace so terminal content and actions remain fully visible above it.
- The compact bottom navigation keeps Home, Library, Activity, and **More** in equal-width tracks derived from its contents. Integrations, Settings, appearance, and session controls live in the More sheet instead of crowding the primary bar.
- Data surfaces grow with sparse content and cap their height only when a real list needs internal scrolling. Empty viewport-filling panels are not used as decoration.

## Information architecture

- **Home:** current playback and listeners first; source route, scrobble delivery, health, and work follow.
- **Library:** playlists, mappings, cached files, and kept files share one task vocabulary and aligned tables.
- **Intelligence (deferred):** excluded from first-release navigation. Existing deep links remain usable during development; hiding navigation does not disable backend work or remove data. Its retained workspace owns Overview, History, Import, Discover, Playlists, and Automation. See the release boundary in [the release plan](docs/release-readiness.md).
- **Integrations:** Services owns provider configuration and diagnostics. Extensions owns package lifecycle. Accounts and Routing explain their scope in plain language.
- **Activity:** outcome, actor, target, duration, and time are primary; technical payloads are progressive detail.
- **Settings:** deployment and operator controls only. User-scoped controls stay near the data they affect.

## Interaction rules

- One clear primary action per task area.
- Dense lists and tables are preferred when comparison matters; spacious guidance is used for onboarding and empty states.
- Modals protect destructive or interrupting work only. Routine configuration stays inline or in a route-level detail pane.
- Administrative Jellyfin controls appear only when the authenticated backend permission explicitly allows them. The UI never infers authorization.
- Loading uses skeletons; empty states explain the next action; errors name both the problem and recovery.

## Motion

- Use 150–250ms Material-style state transitions for selection, reveal, progress, and completion.
- No decorative page-load choreography, looping control animation, or movement that reorders content under the pointer.
- Respect reduced motion. Expensive media effects are lazy, bounded, paused off-screen, and disposable on unmount.

## Quality bar

- Core tasks work at 320px, keyboard-only, reduced-motion, light, and dark themes.
- Status is never color-only.
- Tables share gutters, row heights, alignment, and action placement.
- Shared-system changes should delete route-specific CSS over time and keep the current JS/CSS budgets green.
