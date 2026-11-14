/**
 * A `<input type="datetime-local">` holds a wall-clock time with no zone ("2026-11-20T10:00"). Sent as it is, the server reads it as UTC,
 * so what the person meant as 10:00 where they sit is stored as 10:00Z and reads as a different time for everyone else (and for them).
 * These two convert between that wall time and a real instant, using the browser's own offset on the day in question.
 */

const pad = (n: number) => String(n).padStart(2, '0');

/** "2026-11-20T10:00" (the browser's wall time) to the instant it names, "2026-11-20T07:00:00.000Z" in Riyadh. Blank, a date without a time, or an invalid value is blank. */
export function localInputToInstant(value: string | null | undefined): string {
  const v = (value ?? '').trim();
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$/.test(v)) return '';
  const d = new Date(v); // no zone in the text: JavaScript reads it as the browser's local time
  return Number.isNaN(d.getTime()) ? '' : d.toISOString();
}

/** An instant (any ISO text with a zone) to the browser's wall time for a datetime-local input, "YYYY-MM-DDTHH:mm". Blank or invalid is blank. */
export function instantToLocalInput(instant: string | null | undefined): string {
  const v = (instant ?? '').trim();
  if (!v) return '';
  const d = new Date(v);
  if (Number.isNaN(d.getTime())) return '';
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/**
 * A date with no time ("2026-12-15") that means "by the end of that day", as the instant 23:59:59 on it in the browser's zone,
 * "2026-12-15T20:59:59.000Z" in Riyadh. Sent as the bare date the server reads it as 00:00Z, which is 03:00 in Riyadh on the day itself,
 * so a deadline of the 15th lapsed in the small hours of the 15th. Blank or anything but YYYY-MM-DD is blank.
 */
export function endOfLocalDayInstant(value: string | null | undefined): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec((value ?? '').trim());
  if (!m) return '';
  const d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]), 23, 59, 59, 0);
  // new Date() rolls an impossible day ("2026-02-31") into the next month; that is not the day the person chose.
  if (Number.isNaN(d.getTime()) || d.getMonth() !== Number(m[2]) - 1 || d.getDate() !== Number(m[3])) return '';
  return d.toISOString();
}

// ── The tenant's zone ────────────────────────────────────────────────────────────────────────────────────────────
// A deadline is shown everywhere in the tenant's zone (Asia/Riyadh for a Saudi tenant). A form that used the browser's own zone
// showed a different day and hour from the list beside it whenever the browser sat elsewhere. These two work in a named zone.

function wallParts(ms: number, timeZone: string) {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }).formatToParts(new Date(ms));
  const get = (type: string) => Number(parts.find(p => p.type === type)?.value);
  return { year: get('year'), month: get('month'), day: get('day'), hour: get('hour') % 24, minute: get('minute'), second: get('second') };
}

/** An instant to the wall time of `timeZone`, "YYYY-MM-DDTHH:mm". A blank zone means the browser's own. Blank or invalid is blank. */
export function instantToZonedInput(instant: string | null | undefined, timeZone: string | null | undefined): string {
  if (!timeZone) return instantToLocalInput(instant);
  const v = (instant ?? '').trim();
  const ms = v ? new Date(v).getTime() : NaN;
  if (Number.isNaN(ms)) return '';
  try {
    const p = wallParts(ms, timeZone);
    return `${p.year}-${pad(p.month)}-${pad(p.day)}T${pad(p.hour)}:${pad(p.minute)}`;
  } catch { return instantToLocalInput(instant); }
}

/** A wall time in `timeZone` ("2026-11-19T02:43") to the instant it names. A blank zone means the browser's own. A date without a time is blank. */
export function zonedInputToInstant(value: string | null | undefined, timeZone: string | null | undefined): string {
  if (!timeZone) return localInputToInstant(value);
  const v = (value ?? '').trim();
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(v);
  if (!m) return '';
  const wall = Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3]), Number(m[4]), Number(m[5]));
  try {
    // The zone's offset at that moment: how far its wall clock sits from UTC. Two passes settle a guess made across a daylight-saving change.
    let guess = wall;
    for (let i = 0; i < 2; i++) {
      const p = wallParts(guess, timeZone);
      guess = wall - (Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second) - guess);
    }
    return new Date(guess).toISOString();
  } catch { return localInputToInstant(value); }
}
