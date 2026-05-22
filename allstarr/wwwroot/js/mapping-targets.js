// Shared helpers for displaying Spotify/global external mapping targets.

import { escapeHtml, capitalizeProvider } from "./utils.js";

/**
 * Normalizes external targets from API payloads (camelCase or PascalCase).
 */
export function collectExternalTargets(mapping) {
  const targets = [];
  const seenProviders = new Set();

  const addTarget = (provider, externalId, source) => {
    if (!provider || !externalId) {
      return;
    }

    const key = String(provider).toLowerCase();
    if (seenProviders.has(key)) {
      return;
    }

    seenProviders.add(key);
    targets.push({
      provider: String(provider),
      externalId: String(externalId),
      source: source || "",
    });
  };

  const externalTargets =
    mapping.externalTargets ||
    mapping.ExternalTargets ||
    mapping.externalMappings ||
    mapping.ExternalMappings ||
    [];

  for (const ext of externalTargets) {
    addTarget(
      ext.provider ?? ext.Provider,
      ext.externalId ?? ext.ExternalId,
      ext.source ?? ext.Source,
    );
  }

  addTarget(
    mapping.externalProvider ?? mapping.ExternalProvider,
    mapping.externalId ?? mapping.ExternalId,
    mapping.source ?? mapping.Source ?? "manual",
  );

  return targets;
}

/**
 * Renders a stacked list of provider targets for dashboard tables.
 */
export function renderExternalTargetsHtml(targets, options = {}) {
  const { showRemove = false, playlist = "", spotifyId = "" } = options;

  if (!Array.isArray(targets) || targets.length === 0) {
    return '<span style="color:var(--text-secondary);">—</span>';
  }

  return `<div class="target-list">${targets
    .map((target) => {
      const label = capitalizeProvider(target.provider) || target.provider;
      const removeBtn = showRemove
        ? `<button type="button" class="target-remove-btn delete-mapping-provider-btn"
                data-playlist="${escapeHtml(playlist)}"
                data-spotify-id="${escapeHtml(spotifyId)}"
                data-provider="${escapeHtml(target.provider)}"
                title="Remove ${escapeHtml(label)} mapping">×</button>`
        : "";

      const sourceHint = target.source
        ? `<span class="target-source">${escapeHtml(target.source)}</span>`
        : "";

      return `<div class="target-item">
                <span class="status-pill info">${escapeHtml(label)}</span>
                <span class="mono target-id">${escapeHtml(target.externalId)}</span>
                ${sourceHint}
                ${removeBtn}
            </div>`;
    })
    .join("")}</div>`;
}
