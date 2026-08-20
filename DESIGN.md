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

## Information architecture

- **Home:** current playback and listeners first; source route, scrobble delivery, health, and work follow.
- **Library:** playlists, mappings, cached files, and kept files share one task vocabulary and aligned tables.
- **Intelligence:** Overview → History → Import → Discover → Automation. Import is a top-level task, not a setting hidden inside history.
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
