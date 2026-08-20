export type ThemeMode = "system" | "light" | "dark";

export const themeOptions = [
  { value: "system", label: "Use device setting" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
] as const;

const storageKey = "allstarr.theme";
const changeEvent = "allstarr-theme-change";

export function readThemeMode(): ThemeMode {
  if (typeof localStorage === "undefined") return "system";
  const saved = localStorage.getItem(storageKey);
  return saved === "light" || saved === "dark" ? saved : "system";
}

export function applyThemeMode(mode: ThemeMode) {
  if (typeof document === "undefined") return;
  const dark = mode === "dark" || (mode === "system" && matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.classList.toggle("dark", dark);
  document.documentElement.dataset.theme = mode;
  document.querySelector('meta[name="theme-color"]')?.setAttribute("content", dark ? "#111318" : "#f8fafd");
}

export function saveThemeMode(mode: ThemeMode) {
  localStorage.setItem(storageKey, mode);
  applyThemeMode(mode);
  window.dispatchEvent(new CustomEvent<ThemeMode>(changeEvent, { detail: mode }));
}

export function onThemeModeChange(listener: (mode: ThemeMode) => void) {
  const handler = (event: Event) => listener((event as CustomEvent<ThemeMode>).detail);
  window.addEventListener(changeEvent, handler);
  return () => window.removeEventListener(changeEvent, handler);
}
