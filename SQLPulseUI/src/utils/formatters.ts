/**
 * Shared display formatters.
 *
 * These are presentation-only helpers. All analysis, thresholds, severity and
 * classification logic lives in the FastAPI backend — nothing here interprets
 * data, it only renders it consistently.
 */

const EM_DASH = '—';

/** True when a value is a usable finite number. */
function isNum(n: unknown): n is number {
  return typeof n === 'number' && Number.isFinite(n);
}

/**
 * Format milliseconds as a human-readable duration.
 * Falls back to an em dash for missing/invalid input.
 */
export function formatDuration(ms: number | null | undefined): string {
  if (!isNum(ms)) return EM_DASH;
  if (ms < 0) return `-${formatDuration(-ms)}`;
  if (ms === 0) return '0 ms';
  if (ms < 1) return '<1 ms';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} sec`;

  const totalSeconds = Math.round(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m ${seconds}s`;
}

/**
 * Compact number formatting for dense table cells (e.g. 1234567 → "1.23M").
 * Values below 10,000 keep full precision with thousands separators.
 */
export function formatNumber(n: number | null | undefined): string {
  if (!isNum(n)) return EM_DASH;
  if (n < 0) return `-${formatNumber(-n)}`;
  if (n < 10_000) return Math.round(n).toLocaleString();
  if (n < 1_000_000) return `${(n / 1_000).toFixed(1)}K`;
  if (n < 1_000_000_000) return `${(n / 1_000_000).toFixed(2)}M`;
  return `${(n / 1_000_000_000).toFixed(2)}B`;
}

/**
 * Exact integer with thousands separators — use in tooltips where the
 * compact form from {@link formatNumber} would hide precision.
 */
export function formatInteger(n: number | null | undefined): string {
  if (!isNum(n)) return EM_DASH;
  return Math.round(n).toLocaleString();
}

/** Fixed-decimal float display. */
export function formatFloat(n: number | null | undefined, decimals = 1): string {
  if (!isNum(n)) return EM_DASH;
  return n.toFixed(decimals);
}

/** Render a multiplier such as "2.40×". */
export function formatMultiplier(n: number | null | undefined, decimals = 2): string {
  if (!isNum(n)) return EM_DASH;
  return `${n.toFixed(decimals)}×`;
}

/**
 * Canonical date/time format used across every page so timestamps never
 * render inconsistently between tables, drawers and charts.
 */
const dateTimeFormatter = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
});

/** Short form for chart axes and other space-constrained contexts. */
const shortDateTimeFormatter = new Intl.DateTimeFormat(undefined, {
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
});

function parseDate(value: string | number | Date | null | undefined): Date | null {
  if (value == null) return null;
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

/** Format an ISO timestamp using the canonical app-wide format. */
export function formatTimestamp(iso: string | number | Date | null | undefined): string {
  const d = parseDate(iso);
  if (!d) return EM_DASH;
  return dateTimeFormatter.format(d);
}

/** Compact timestamp for chart axis ticks. */
export function formatTimestampShort(iso: string | number | Date | null | undefined): string {
  const d = parseDate(iso);
  if (!d) return EM_DASH;
  return shortDateTimeFormatter.format(d);
}

/** Convert a Date to the `datetime-local` input value format. */
export function toDateTimeLocal(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Collapse whitespace and clip SQL text for inline preview. */
export function truncateSql(sql: string | null | undefined, maxLen = 120): string {
  if (!sql) return EM_DASH;
  const cleaned = sql.replace(/\s+/g, ' ').trim();
  if (!cleaned) return EM_DASH;
  if (cleaned.length <= maxLen) return cleaned;
  return `${cleaned.slice(0, maxLen)}…`;
}

/** Collapse whitespace without clipping — for tooltips and expanded views. */
export function normalizeSql(sql: string | null | undefined): string {
  if (!sql) return '';
  return sql.replace(/\s+/g, ' ').trim();
}

/** Shorten a long hash for table display while keeping it recognisable. */
export function shortenHash(hash: string | null | undefined, len = 16): string {
  if (!hash) return EM_DASH;
  return hash.length <= len ? hash : `${hash.slice(0, len)}…`;
}

/** Fallback for any nullable display value. */
export function orDash(value: string | number | null | undefined): string {
  if (value == null || value === '') return EM_DASH;
  return String(value);
}
