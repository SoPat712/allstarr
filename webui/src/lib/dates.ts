type LocalDateTimeOptions = { locale?: string; timeZone?: string };

export function formatLocalDateTime(value?: string | null, fallback = "Time unavailable", options: LocalDateTimeOptions = {}) {
  const date = value ? new Date(value) : null;
  if (!date || !Number.isFinite(date.getTime())) return fallback;
  return new Intl.DateTimeFormat(options.locale, {
    month: "short", day: "numeric", hour: "numeric", minute: "2-digit",
    ...(options.timeZone ? { timeZone: options.timeZone } : {}),
  }).format(date);
}
