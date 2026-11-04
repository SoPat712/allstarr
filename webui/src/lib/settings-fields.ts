import type { ConfigField } from "$lib/api";
import { pathValue } from "$lib/settings";

export function settingValue(config: Record<string, unknown>, field: ConfigField): string {
  if (field.sensitive) return "";
  const value = pathValue(config, field.valuePath);
  if (field.type === "toggle" || field.type === "boolean") return String(value === true || String(value).toLowerCase() === "true");
  return value == null ? "" : String(value);
}
