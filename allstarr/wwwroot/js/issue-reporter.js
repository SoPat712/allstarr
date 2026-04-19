import { showToast } from "./utils.js";

const GITHUB_NEW_ISSUE_URL = "https://github.com/SoPat712/allstarr/issues/new";
const MAX_PREFILL_URL_LENGTH = 6500;
const ISSUE_TEMPLATES = {
  bug: {
    template: "bug-report.md",
    titlePrefix: "[BUG] ",
    openLabel: "Open Bug Report on GitHub",
    primaryLabel: "Describe the bug",
    primaryPlaceholder: "What happened? What looked wrong?",
    secondaryLabel: "To Reproduce",
    secondaryPlaceholder: "List the steps needed to reproduce the issue",
    tertiaryLabel: "Expected behavior",
    tertiaryPlaceholder: "What did you expect to happen instead?",
    contextLabel: "Additional context",
    contextPlaceholder:
      "Anything else that might help, including screenshots or surrounding context",
  },
  feature: {
    template: "feature-request.md",
    titlePrefix: "[FEATURE] ",
    openLabel: "Open Feature Request on GitHub",
    primaryLabel: "Problem to solve",
    primaryPlaceholder: "What problem are you trying to solve?",
    secondaryLabel: "Solution you'd like",
    secondaryPlaceholder: "What should Allstarr do instead?",
    tertiaryLabel: "Alternatives considered",
    tertiaryPlaceholder: "What alternatives or workarounds have you considered?",
    contextLabel: "Additional context",
    contextPlaceholder:
      "Extra examples, mockups, or screenshots that explain the request",
  },
};
const DIAGNOSTIC_SOURCE_IDS = [
  "sidebar-version",
  "backend-type",
  "spotify-status",
  "jellyfin-url",
  "config-music-service",
  "config-storage-mode",
  "config-download-mode",
  "config-redis-enabled",
  "config-spotify-import-enabled",
  "config-deezer-quality",
  "config-squid-quality",
  "config-qobuz-quality",
  "scrobbling-enabled-value",
];

function getElement(id) {
  return document.getElementById(id);
}

function normalizeText(value, fallback = "Unavailable") {
  const normalized = String(value ?? "").trim();
  if (!normalized || normalized === "-" || /^loading/i.test(normalized)) {
    return fallback;
  }

  return normalized;
}

function getIssueType() {
  return getElement("issue-report-type")?.value === "feature" ? "feature" : "bug";
}

function getIssueConfig(type = getIssueType()) {
  return ISSUE_TEMPLATES[type] || ISSUE_TEMPLATES.bug;
}

function sanitizeTitle(title, type) {
  const prefix = getIssueConfig(type).titlePrefix;
  const trimmed = String(title ?? "").trim();
  if (!trimmed) {
    return prefix + (type === "feature" ? "Please add a short request title" : "Please add a short bug title");
  }

  if (trimmed.toUpperCase().startsWith(prefix.trim())) {
    return trimmed;
  }

  return prefix + trimmed;
}

function getElementText(id, fallback = "Unavailable") {
  return normalizeText(getElement(id)?.textContent, fallback);
}

function getMusicServiceQuality(musicService) {
  const normalized = String(musicService ?? "").trim().toLowerCase();
  if (normalized === "deezer") {
    return getElementText("config-deezer-quality");
  }
  if (normalized === "qobuz") {
    return getElementText("config-qobuz-quality");
  }
  if (normalized === "squidwtf") {
    return getElementText("config-squid-quality");
  }

  return "";
}

function getClientSummary() {
  const ua = String(window.navigator?.userAgent ?? "");
  const browser =
    ua.match(/Firefox\/(\d+)/)?.[0]?.replace("/", " ") ||
    ua.match(/Edg\/(\d+)/)?.[0]?.replace("/", " ") ||
    ua.match(/Chrome\/(\d+)/)?.[0]?.replace("/", " ") ||
    (ua.includes("Safari/") && ua.match(/Version\/(\d+)/)?.[0]?.replace("/", " ")) ||
    "Unknown browser";

  let platform = "Unknown OS";
  if (/Mac OS X/i.test(ua)) {
    platform = "macOS";
  } else if (/Windows/i.test(ua)) {
    platform = "Windows";
  } else if (/Android/i.test(ua)) {
    platform = "Android";
  } else if (/iPhone|iPad|iPod/i.test(ua)) {
    platform = "iOS";
  } else if (/Linux/i.test(ua)) {
    platform = "Linux";
  }

  return `${browser} on ${platform}`;
}

function getRedactedUrlState() {
  const jellyfinUrl = normalizeText(getElement("jellyfin-url")?.textContent, "");
  return jellyfinUrl ? "Configured (redacted)" : "Not configured";
}

function getDiagnostics() {
  const timezone =
    Intl.DateTimeFormat().resolvedOptions().timeZone || "Unavailable";
  const musicService = getElementText("config-music-service");

  return {
    version: getElementText("sidebar-version"),
    backendType: normalizeText(
      getElement("backend-type")?.textContent ||
        getElement("config-backend-type")?.textContent,
    ),
    musicService,
    musicServiceQuality: getMusicServiceQuality(musicService),
    storageMode: getElementText("config-storage-mode"),
    downloadMode: getElementText("config-download-mode"),
    redisEnabled: getElementText("config-redis-enabled"),
    spotifyImportEnabled: getElementText("config-spotify-import-enabled"),
    scrobblingEnabled: getElementText("scrobbling-enabled-value"),
    spotifyStatus: getElementText("spotify-status"),
    jellyfinUrl: getRedactedUrlState(),
    client: getClientSummary(),
    generatedAt: new Date().toISOString(),
    timezone,
  };
}

function getReportState() {
  const type = getIssueType();
  return {
    type,
    titleInput: String(getElement("issue-report-title")?.value ?? "").trim(),
    primary: String(getElement("issue-report-primary")?.value ?? "").trim(),
    secondary: String(getElement("issue-report-secondary")?.value ?? "").trim(),
    tertiary: String(getElement("issue-report-tertiary")?.value ?? "").trim(),
    context: String(getElement("issue-report-context")?.value ?? "").trim(),
  };
}

function renderIssueBody(state, includeDiagnostics = true) {
  const diagnostics = getDiagnostics();
  const diagnosticsLines = [
    "<details>",
    "<summary>Safe diagnostics from Allstarr</summary>",
    "",
    "- Sensitive values stay redacted in this block.",
    `- Allstarr Version: ${diagnostics.version}`,
    `- Backend Type: ${diagnostics.backendType}`,
    `- Music Service: ${diagnostics.musicService}`,
    diagnostics.musicServiceQuality
      ? `- Music Service Quality: ${diagnostics.musicServiceQuality}`
      : null,
    `- Storage Mode: ${diagnostics.storageMode}`,
    `- Download Mode: ${diagnostics.downloadMode}`,
    `- Redis Enabled: ${diagnostics.redisEnabled}`,
    `- Spotify Import Enabled: ${diagnostics.spotifyImportEnabled}`,
    `- Scrobbling Enabled: ${diagnostics.scrobblingEnabled}`,
    `- Spotify Status: ${diagnostics.spotifyStatus}`,
    `- Jellyfin URL: ${diagnostics.jellyfinUrl}`,
    `- Client: ${diagnostics.client}`,
    `- Generated At (UTC): ${diagnostics.generatedAt}`,
    `- Browser Time Zone: ${diagnostics.timezone}`,
    "",
    "</details>",
  ];
  const diagnosticsMarkdown = diagnosticsLines.filter(Boolean).join("\n");

  if (state.type === "feature") {
    const sections = [
      [
        "**Is your feature request related to a problem? Please describe.**",
        state.primary || "_Please describe the problem you want to solve._",
      ],
      [
        "**Describe the solution you'd like**",
        state.secondary || "_Please describe the solution you want._",
      ],
      [
        "**Describe alternatives you've considered**",
        state.tertiary || "_Please describe alternatives or workarounds you've considered._",
      ],
      [
        "**Additional context**",
        state.context || "_Add any other context, screenshots, or examples here._",
      ],
    ];

    if (includeDiagnostics) {
      sections.push(["**Environment**", diagnosticsMarkdown]);
    }

    return sections.map(([heading, content]) => `${heading}\n${content}`).join("\n\n");
  }

  const sections = [
    [
      "**Describe the bug**",
      state.primary || "_Please describe the bug._",
    ],
    [
      "**To Reproduce**",
      state.secondary ||
        "_Please list the steps needed to reproduce the issue._",
    ],
    [
      "**Expected behavior**",
      state.tertiary || "_Please describe what you expected to happen._",
    ],
    [
      "**Additional context**",
      state.context || "_Add any other context, screenshots, or examples here._",
    ],
  ];

  if (includeDiagnostics) {
    sections.push(["**Details**", diagnosticsMarkdown]);
  }

  return sections.map(([heading, content]) => `${heading}\n${content}`).join("\n\n");
}

function buildIssuePayload() {
  const state = getReportState();
  const config = getIssueConfig(state.type);
  const title = sanitizeTitle(state.titleInput, state.type);
  const fullBody = renderIssueBody(state, true);

  const fullUrl = new URL(GITHUB_NEW_ISSUE_URL);
  fullUrl.searchParams.set("template", config.template);
  fullUrl.searchParams.set("title", title);
  fullUrl.searchParams.set("body", fullBody);

  if (fullUrl.toString().length <= MAX_PREFILL_URL_LENGTH) {
    return {
      title,
      fullBody,
      url: fullUrl.toString(),
      truncated: false,
    };
  }

  const shortenedBody = [
    renderIssueBody(state, false),
    "> Full safe diagnostics were copied to your clipboard by Allstarr.",
    "> Paste them below if GitHub opens with a shorter draft.",
  ].join("\n\n");

  const shortenedUrl = new URL(GITHUB_NEW_ISSUE_URL);
  shortenedUrl.searchParams.set("template", config.template);
  shortenedUrl.searchParams.set("title", title);
  shortenedUrl.searchParams.set("body", shortenedBody);

  return {
    title,
    fullBody,
    url: shortenedUrl.toString(),
    truncated: true,
  };
}

async function copyTextToClipboard(text) {
  if (!text) {
    return false;
  }

  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Fall back to a hidden textarea if direct clipboard access fails.
    }
  }

  const helper = document.createElement("textarea");
  helper.value = text;
  helper.setAttribute("readonly", "");
  helper.style.position = "absolute";
  helper.style.left = "-9999px";
  document.body.appendChild(helper);
  helper.select();

  let copied = false;
  try {
    copied = document.execCommand("copy");
  } catch {
    copied = false;
  }

  document.body.removeChild(helper);
  return copied;
}

async function copyIssueReport({ silent = false } = {}) {
  const payload = buildIssuePayload();
  const copied = await copyTextToClipboard(`${payload.title}\n\n${payload.fullBody}`);

  if (!silent) {
    showToast(
      copied
        ? "Issue draft copied to clipboard"
        : "Could not copy the report. You can still copy it from the preview.",
      copied ? "success" : "warning",
      4000,
    );
  }

  return copied;
}

function validateTitle() {
  const titleInput = getElement("issue-report-title");
  if (!titleInput?.value?.trim()) {
    titleInput?.focus();
    showToast("Add a short title before opening the GitHub draft.", "warning");
    return false;
  }

  return true;
}

async function openGithubIssueDraft() {
  if (!validateTitle()) {
    return;
  }

  const copied = await copyIssueReport({ silent: true });
  const payload = buildIssuePayload();
  const openedWindow = window.open(payload.url, "_blank", "noopener,noreferrer");

  if (!openedWindow) {
    window.location.href = payload.url;
  }

  const message = payload.truncated
    ? "Opened a shorter GitHub draft and copied the full report to your clipboard."
    : copied
      ? "Opened the GitHub draft and copied the report to your clipboard."
      : "Opened the GitHub draft. If anything is missing, use Copy Report.";
  showToast(message, payload.truncated ? "warning" : "success", 5000);
}

function updateIssueReporterCopy() {
  const type = getIssueType();
  const config = getIssueConfig(type);

  getElement("issue-report-primary-label").textContent = config.primaryLabel;
  getElement("issue-report-primary").placeholder = config.primaryPlaceholder;
  getElement("issue-report-secondary-label").textContent = config.secondaryLabel;
  getElement("issue-report-secondary").placeholder = config.secondaryPlaceholder;
  getElement("issue-report-tertiary-label").textContent = config.tertiaryLabel;
  getElement("issue-report-tertiary").placeholder = config.tertiaryPlaceholder;
  getElement("issue-report-context-label").textContent = config.contextLabel;
  getElement("issue-report-context").placeholder = config.contextPlaceholder;
  getElement("open-github-issue-btn").textContent = config.openLabel;
  getElement("issue-report-title").placeholder =
    type === "feature"
      ? "Short summary of the feature request"
      : "Short summary of the issue";
}

export function refreshIssueReportPreview() {
  const preview = getElement("issue-report-preview");
  const previewHelp = getElement("issue-report-preview-help");
  if (!preview || !previewHelp) {
    return;
  }

  updateIssueReporterCopy();

  const payload = buildIssuePayload();
  preview.value = `${payload.title}\n\n${payload.fullBody}`;
  previewHelp.textContent = payload.truncated
    ? "This report is long enough that Allstarr will open GitHub with a shorter draft and copy the full report to your clipboard."
    : "This draft fits in a normal GitHub issue URL. Allstarr will still copy the full report to your clipboard when you open it.";
}

export function initIssueReporter() {
  const typeSelect = getElement("issue-report-type");
  const titleInput = getElement("issue-report-title");
  const primaryInput = getElement("issue-report-primary");
  const secondaryInput = getElement("issue-report-secondary");
  const tertiaryInput = getElement("issue-report-tertiary");
  const contextInput = getElement("issue-report-context");
  const copyButton = getElement("copy-issue-report-btn");
  const openButton = getElement("open-github-issue-btn");

  if (
    !typeSelect ||
    !titleInput ||
    !primaryInput ||
    !secondaryInput ||
    !tertiaryInput ||
    !contextInput ||
    !copyButton ||
    !openButton
  ) {
    return;
  }

  [typeSelect, titleInput, primaryInput, secondaryInput, tertiaryInput, contextInput].forEach(
    (input) => {
      input.addEventListener("input", refreshIssueReportPreview);
      input.addEventListener("change", refreshIssueReportPreview);
    },
  );

  copyButton.addEventListener("click", () => {
    copyIssueReport();
  });
  openButton.addEventListener("click", () => {
    openGithubIssueDraft();
  });

  const diagnosticsObserver = new MutationObserver(() => {
    refreshIssueReportPreview();
  });
  DIAGNOSTIC_SOURCE_IDS.forEach((id) => {
    const source = getElement(id);
    if (!source) {
      return;
    }

    diagnosticsObserver.observe(source, {
      childList: true,
      subtree: true,
      characterData: true,
    });
  });

  window.addEventListener("hashchange", refreshIssueReportPreview);
  refreshIssueReportPreview();
}
