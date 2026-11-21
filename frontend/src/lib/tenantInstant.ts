/**
 * The moment a person means by "18:00 on 2 Dec" is a clock time in the COMPANY's time zone, not the zone of the
 * browser they happen to be using. `new Date('2026-12-02T18:00:00')` reads it in the browser zone, so an HR user on
 * a laptop in another zone filed overtime the server then refused ("work date must be the date it started").
 *
 * Returns the UTC instant (ISO) for `date` (yyyy-mm-dd) + `time` (hh:mm) read in `timeZone`. An empty or unknown zone
 * falls back to the browser zone, which is the old behaviour.
 */
export function tenantLocalInstantIso(date: string, time: string, timeZone: string): string {
  const browserIso = () => new Date(`${date}T${time}:00`).toISOString();
  if (!timeZone) return browserIso();
  try {
    const wanted = Date.parse(`${date}T${time}:00Z`); // the wall clock, pretended to be UTC
    if (Number.isNaN(wanted)) return browserIso();
    const fmt = new Intl.DateTimeFormat('en-CA', {
      timeZone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
    });
    const offsetAt = (utcMs: number) => {
      const p = Object.fromEntries(fmt.formatToParts(new Date(utcMs)).map(x => [x.type, x.value]));
      const asUtc = Date.UTC(+p.year, +p.month - 1, +p.day, +p.hour, +p.minute, +p.second);
      return asUtc - utcMs;
    };
    // Two passes settle the offset for a zone whose offset changes within a day of the guess.
    let utc = wanted - offsetAt(wanted);
    utc = wanted - offsetAt(utc);
    return new Date(utc).toISOString();
  } catch {
    return browserIso();
  }
}
