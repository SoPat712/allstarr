# Project Progress

## Active deployment package: extension health, configuration, and lyrics cleanup

- Route: Heavy; root owns integration, verification, status, and deployment.
- Baseline: `dev` and `origin/dev` at `6a7680aae655fda2e995e7bf7d7f3408879a1d09`.
- Constraints: preserve unrelated dirty/untracked work; no new framework or duplicate health store; browser-only UI verification; no Musiver, Computer Use, or automated Feishin; live GAMDL generation is cached-only without separate bounded authorization.

## Checklist

- [x] EXT-1 Canonicalize SpotiFLAC package identity, truthful update projection, and active permission review.
- [x] SRC-1 Make Sources the single provider configuration, account, health, and diagnostics home; normalize shared UI geometry.
- [x] CTS-1 Retain account-free probe latency and schedule only bounded account-free typed-stream CTS.
- [x] LYR-1 Remove LyricsPlus and complete source-native/Apple/fallback lyrics routing.
- [ ] VER-1 Pass focused tests, the complete release matrix, browser smoke, push, authorized deployment, and live read-only verification.

## Acceptance gates

- Installed catalog versions are hidden; newer versions show an explicit update action.
- Permission-bearing active extensions can enter review-required state safely; permissionless packages cannot offer the action.
- Settings owns extension lifecycle only; Sources owns provider configuration and exact deep links.
- Timing cells distinguish latest API latency, managed p95, CTS, not applicable, manual-only, and awaiting-sample states.
- Production and configuration contain no LyricsPlus owner or executable reference.
- Apple extension settings and GAMDL lyrics remain distinct, and provider failure continues to the next distinct lyrics fallback.
- No protocol break, new dependency, duplicate health table, unrelated staged file, or destructive live media operation.

## Next action

Commit the verified revision, push `dev`, deploy through `allstarr.sh update`, then complete browser-only live verification.
