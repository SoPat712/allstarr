# Admin UI Modularity Guide

This document defines the modular JavaScript architecture for `allstarr/wwwroot/js` and the guardrails future agents should follow.

## Goals

- Keep admin UI code split by feature and responsibility.
- Centralize request handling and async UI action handling.
- Minimize `window.*` globals to only those required by inline HTML handlers.
- Keep polling and refresh lifecycle in one place.

## Current Module Map

- `main.js`: Composition root only. Wires modules, shared globals, and bootstrap lifecycle.
- `auth-session.js`: Auth/session state, role-based scope, login/logout wiring, 401 recovery handling.
- `dashboard-data.js`: Polling lifecycle + data loading/render orchestration.
- `operations.js`: Shared `runAction` helper + non-domain operational actions.
- `settings-editor.js`: Settings registry, modal editor rendering, local config state sync.
- `playlist-admin.js`: Playlist linking and admin CRUD.
- `scrobbling-admin.js`: Scrobbling configuration actions and UI state updates.
- `api.js`: API transport layer wrappers and endpoint functions.

## Required Patterns

### 1) Request Layer Rules

- All HTTP requests must go through `api.js`.
- `api.js` owns low-level `fetch` usage (`requestJson`, `requestBlob`, `requestOptionalJson`).
- Feature modules should call `API.*` methods and avoid direct `fetch`.

### 2) Action Flow Rules

- UI actions with toast/error handling should use `runAction(...)` from `operations.js`.
- If an action always reloads scrobbling UI state, use `runScrobblingAction(...)` in `scrobbling-admin.js`.

### 3) Polling Rules

- Polling timers must stay in `dashboard-data.js`.
- New background refresh loops should be added to existing refresh lifecycle, not separate timers in other modules.

### 4) Global Surface Rules

- Expose only `window.*` members needed by current inline HTML (`onclick`, `onchange`, `oninput`) or legacy UI templates.
- Keep new feature logic module-scoped and expose narrow entry points in `init*` functions.

## Adding New Admin UI Behavior

1. Add/extend endpoint method in `api.js`.
2. Implement feature logic in the relevant module (`*-admin.js`, `dashboard-data.js`, etc.).
3. Prefer `runAction(...)` for async UI operations.
4. Export/init through module `init*` only.
5. Wire it from `main.js` if cross-module dependencies are needed.
6. Add/adjust tests in `allstarr.Tests/JavaScriptSyntaxTests.cs`.

## Tests That Enforce This Architecture

`allstarr.Tests/JavaScriptSyntaxTests.cs` includes checks for:

- Module existence and syntax.
- Coordinator bootstrap expectations.
- API request centralization (`fetch` calls constrained to helper functions in `api.js`).
- Scrobbling module prohibition on direct `fetch`.

## Fast Validation Commands

```bash
# Full suite
 dotnet test allstarr.sln

# JS architecture/syntax focused
 dotnet test allstarr.Tests/allstarr.Tests.csproj --filter JavaScriptSyntaxTests
```
