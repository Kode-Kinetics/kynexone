const RIYADH_TIME_ZONE = 'Asia/Riyadh';

/** Calendar date used by the Saudi workforce API, independent of device/UTC date. */
export function riyadhBusinessDate(value: Date = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: RIYADH_TIME_ZONE,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(value);
  const part = (type: Intl.DateTimeFormatPartTypes) =>
    parts.find((entry) => entry.type === type)?.value;
  const year = part('year');
  const month = part('month');
  const day = part('day');
  if (!year || !month || !day) throw new Error('Unable to resolve the Riyadh business date.');
  return `${year}-${month}-${day}`;
}

export function riyadhBusinessMonth(value: Date = new Date()): string {
  return riyadhBusinessDate(value).slice(0, 7);
}

export function formatRiyadhBusinessDate(value: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-US', {
    timeZone: RIYADH_TIME_ZONE,
    weekday: 'long',
    year: 'numeric',
    month: 'long',
    day: 'numeric',
  }).format(value);
}
