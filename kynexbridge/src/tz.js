// Device wall-clock -> UTC, using the DEVICE's configured IANA time zone, never the bridge host's.
//
// ZKTeco and most terminals store local wall-clock time with no offset. node-zklib builds a JS Date from those
// fields in the HOST's zone, so a UTC Docker container pointing at a Riyadh (UTC+3) device shifted every punch by
// 3 hours. The wall-clock fields are recovered from the Date (host-local getters) and re-read in the device zone.

const fmtCache = new Map();
function dtf(timeZone) {
  let f = fmtCache.get(timeZone);
  if (!f) {
    f = new Intl.DateTimeFormat('en-US', {
      timeZone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit',
    });
    fmtCache.set(timeZone, f);
  }
  return f;
}

export function assertTimeZone(timeZone) {
  try { dtf(timeZone); } catch { throw new Error(`"${timeZone}" is not a valid IANA time zone (e.g. "Asia/Riyadh").`); }
}

// Offset (ms) of `timeZone` at the UTC instant `utcMs`.
function offsetAt(utcMs, timeZone) {
  const parts = Object.fromEntries(dtf(timeZone).formatToParts(new Date(utcMs)).map((p) => [p.type, p.value]));
  const asUtc = Date.UTC(+parts.year, +parts.month - 1, +parts.day, +parts.hour, +parts.minute, +parts.second);
  return asUtc - Math.floor(utcMs / 1000) * 1000;
}

/** Wall-clock fields in `timeZone` -> UTC ISO string. A time skipped by DST resolves forward; a repeated one to the first. */
export function wallClockToUtcIso({ year, month, day, hour = 0, minute = 0, second = 0 }, timeZone) {
  assertTimeZone(timeZone);
  const naive = Date.UTC(year, month - 1, day, hour, minute, second);
  let guess = naive - offsetAt(naive, timeZone);
  guess = naive - offsetAt(guess, timeZone);   // second pass settles across a DST boundary
  return new Date(guess).toISOString();
}

/** A Date whose HOST-local fields are the device's wall clock (what node-zklib returns) -> UTC ISO in the device zone. */
export function hostLocalDateToUtcIso(date, timeZone) {
  return wallClockToUtcIso({
    year: date.getFullYear(), month: date.getMonth() + 1, day: date.getDate(),
    hour: date.getHours(), minute: date.getMinutes(), second: date.getSeconds(),
  }, timeZone);
}

/** Parses a timestamp string. An explicit offset/Z wins; a bare wall-clock string is read in `timeZone`. */
export function parseDeviceTimestamp(raw, timeZone) {
  const s = String(raw).trim();
  if (/(Z|[+-]\d{2}:?\d{2})$/i.test(s)) {
    const d = new Date(s);
    return Number.isNaN(d.getTime()) ? null : d.toISOString();
  }
  const m = s.match(/^(\d{4})[-/](\d{1,2})[-/](\d{1,2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?$/);
  if (!m) return null;
  if (!timeZone) return null;
  return wallClockToUtcIso({ year: +m[1], month: +m[2], day: +m[3], hour: +(m[4] || 0), minute: +m[5] || 0, second: +(m[6] || 0) }, timeZone);
}
