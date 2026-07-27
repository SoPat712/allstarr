import type { ConfigField, PriorityGroup } from "./api";

export function pathValue(source: Record<string, unknown>, path?: string | null): unknown {
  return path?.split(".").reduce<unknown>(
    (value, key) => value && typeof value === "object"
      ? (value as Record<string, unknown>)[key]
      : undefined,
    source,
  );
}

export function fieldValue(source: Record<string, unknown>, field: ConfigField) {
  const value = pathValue(source, field.valuePath);
  return field.type === "toggle" ? Boolean(value) : value == null ? "" : String(value);
}

export function routingOrder(config: Record<string, unknown>, group: PriorityGroup) {
  const providers = pathValue(config, `providers.${group.id}Order`);
  const configured = typeof providers === "string"
    ? providers.split(",").map((value) => value.trim()).filter(Boolean)
    : [];
  return [...configured, ...group.providers.filter((provider) => !configured.includes(provider))];
}

export function move<T>(items: T[], index: number, direction: -1 | 1) {
  const next = index + direction;
  if (next < 0 || next >= items.length) return items;
  const result = [...items];
  [result[index], result[next]] = [result[next], result[index]];
  return result;
}
