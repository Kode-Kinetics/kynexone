/**
 * Location pinning for Setup (create-company wizard, step 3). Everything here runs in the browser: a pasted Google
 * Maps link, OpenStreetMap or Apple Maps link, plain "21.5433, 39.1728" or degrees-minutes-seconds become
 * coordinates without any request. Only a Google short link (maps.app.goo.gl, goo.gl/maps) carries no coordinates;
 * it is returned as `short-link` for the backend resolver (api/geo.ts).
 */

export type ParsedLocation =
  | { kind: 'coordinates'; latitude: number; longitude: number; from: 'link' | 'text' }
  | { kind: 'short-link'; url: string }
  | { kind: 'unreadable' };

export const RADIUS_PRESETS = [50, 100, 150, 300] as const;

/** Six decimals is about 11 cm: finer than any phone fix, and within the database's 7. */
export const roundCoordinate = (value: number) => Math.round(value * 1e6) / 1e6;
export const formatCoordinates = (latitude: number, longitude: number) => `${latitude.toFixed(6)}, ${longitude.toFixed(6)}`;

/** Opens the point in Google Maps (the documented Maps URLs form). */
export const googleMapsUrl = (latitude: number, longitude: number) =>
  `https://www.google.com/maps/search/?api=1&query=${latitude},${longitude}`;

/** A generous box around the six GCC states (Oman's south to Kuwait's north, the Red Sea coast to Oman's east). */
const GCC = { minLat: 16, maxLat: 32.5, minLng: 34, maxLng: 60 };
export const isInGcc = (latitude: number, longitude: number) =>
  latitude >= GCC.minLat && latitude <= GCC.maxLat && longitude >= GCC.minLng && longitude <= GCC.maxLng;
/** True when the pair reads as a GCC point only the other way round: the usual paste mistake. */
export const looksSwapped = (latitude: number, longitude: number) => !isInGcc(latitude, longitude) && isInGcc(longitude, latitude);

/** Where the map opens before anything is pinned: the country's capital. Never saved as a pin. */
export const COUNTRY_CENTRE: Record<string, [number, number]> = {
  SA: [24.7136, 46.6753], AE: [24.4539, 54.3773], QA: [25.2854, 51.531], KW: [29.3759, 47.9774], BH: [26.2235, 50.5876], OM: [23.588, 58.3829],
};
export const countryCentre = (code: string | undefined): [number, number] => COUNTRY_CENTRE[(code ?? '').toUpperCase()] ?? COUNTRY_CENTRE.SA;

const validPair = (latitude: number, longitude: number) =>
  Number.isFinite(latitude) && Number.isFinite(longitude) && Math.abs(latitude) <= 90 && Math.abs(longitude) <= 180;

/** Arabic-Indic and Persian digits, the Arabic decimal mark and comma, and full-width spaces, as ASCII. */
function normalizeDigits(text: string): string {
  return text
    .replace(/[٠-٩]/g, d => String(d.charCodeAt(0) - 0x0660))
    .replace(/[۰-۹]/g, d => String(d.charCodeAt(0) - 0x06f0))
    .replace(/٫/g, '.')
    .replace(/[،٬]/g, ',')
    .replace(/[  -​　]/g, ' ');
}

const NUM = String.raw`[-+]?\d{1,3}(?:\.\d+)?`;
const PAIR = new RegExp(String.raw`^\s*(${NUM})\s*,\s*\+?\s*(${NUM})\s*$`);

function pair(value: string | null | undefined): [number, number] | null {
  const m = value ? PAIR.exec(value) : null;
  if (!m) return null;
  const latitude = Number(m[1]);
  const longitude = Number(m[2]);
  return validPair(latitude, longitude) ? [latitude, longitude] : null;
}

function asCoordinates(found: [number, number] | null, from: 'link' | 'text'): ParsedLocation | null {
  return found ? { kind: 'coordinates', latitude: roundCoordinate(found[0]), longitude: roundCoordinate(found[1]), from } : null;
}

/** Google's own domains (the GCC's and the usual others). A host is Google Maps only on one of these, exactly. */
const GOOGLE_DOMAINS = new Set([
  'google.com', 'google.com.sa', 'google.ae', 'google.com.qa', 'google.com.kw', 'google.com.bh', 'google.com.om',
  'google.co.uk', 'google.co.in', 'google.com.pk', 'google.com.eg', 'google.jo', 'google.com.lb',
]);
/** Exact hostname matches only: "google.com.evil.example" and "evilgoogle.com" are not Google. */
export function isGoogleMapsHost(host: string): boolean {
  const h = host.toLowerCase();
  const bare = h.startsWith('www.') ? h.slice(4) : h.startsWith('maps.') ? h.slice(5) : h;
  return GOOGLE_DOMAINS.has(bare);
}
export const isOpenStreetMapHost = (host: string) => { const h = host.toLowerCase(); return h === 'openstreetmap.org' || h.endsWith('.openstreetmap.org'); };
export const isAppleMapsHost = (host: string) => host.toLowerCase() === 'maps.apple.com';

function parseUrl(text: string): ParsedLocation | null {
  let url: URL;
  try {
    url = new URL(/^[a-z][a-z0-9+.-]*:\/\//i.test(text) ? text : `https://${text}`);
  } catch {
    return null;
  }
  if (!/^https?:$/.test(url.protocol)) return null;
  const host = url.hostname.toLowerCase();
  if (host === 'maps.app.goo.gl' || (host === 'goo.gl' && url.pathname.startsWith('/maps/'))) {
    url.protocol = 'https:';
    return { kind: 'short-link', url: url.toString() };
  }
  let decoded: string;
  try { decoded = decodeURIComponent(url.href); } catch { decoded = url.href; }
  const params = url.searchParams;

  if (isGoogleMapsHost(host)) {
    // The place's own pin, then an explicit point, then the map's centre.
    const data = new RegExp(String.raw`!3d(${NUM})!4d(${NUM})`).exec(decoded);
    if (data) return asCoordinates(validPair(+data[1], +data[2]) ? [+data[1], +data[2]] : null, 'link');
    const path = new RegExp(String.raw`/maps/(?:search|place|dir)/(${NUM}),\s*\+?(${NUM})(?:[/?#]|$)`).exec(decoded);
    if (path) return asCoordinates(pair(`${path[1]},${path[2]}`), 'link');
    for (const name of ['q', 'query', 'll', 'center', 'destination', 'daddr']) {
      const found = pair(params.get(name));
      if (found) return asCoordinates(found, 'link');
    }
    const at = new RegExp(String.raw`/@(${NUM}),(${NUM})(?:,|$|/|\?)`).exec(decoded);
    if (at) return asCoordinates(pair(`${at[1]},${at[2]}`), 'link');
    return { kind: 'unreadable' };
  }
  if (isOpenStreetMapHost(host)) {
    const mlat = params.get('mlat');
    const mlon = params.get('mlon');
    if (mlat && mlon) return asCoordinates(pair(`${mlat},${mlon}`), 'link') ?? { kind: 'unreadable' };
    const hash = new RegExp(String.raw`map=\d+/(${NUM})/(${NUM})`).exec(url.hash);
    if (hash) return asCoordinates(pair(`${hash[1]},${hash[2]}`), 'link') ?? { kind: 'unreadable' };
    return { kind: 'unreadable' };
  }
  if (isAppleMapsHost(host)) {
    for (const name of ['ll', 'q', 'coordinate', 'sll']) {
      const found = pair(params.get(name));
      if (found) return asCoordinates(found, 'link');
    }
    return { kind: 'unreadable' };
  }
  return { kind: 'unreadable' };
}

/** "21°32'35.9"N 39°10'22.1"E", with ′ ″ or plain quotes; the hemisphere letter before every part or after every part. */
function parseDms(text: string): [number, number] | null {
  const prefix = /^\s*[NSEW]/i.test(text);
  const body = String.raw`(\d{1,3}(?:\.\d+)?)\s*[°º]\s*(?:(\d{1,2}(?:\.\d+)?)\s*['′’]\s*)?(?:(\d{1,2}(?:\.\d+)?)\s*(?:["″”]|''|′′)\s*)?`;
  const part = prefix ? String.raw`([NSEW])\s*${body}` : String.raw`${body}([NSEW])?`;
  const whole = new RegExp(String.raw`^\s*${part}\s*[,;]?\s*${part}\s*$`, 'i').exec(text);
  if (!whole) return null;
  const read = (offset: number) => {
    const [hemisphere, degrees, minutes = '0', seconds = '0'] = prefix
      ? [whole[offset], whole[offset + 1], whole[offset + 2], whole[offset + 3]]
      : [whole[offset + 3], whole[offset], whole[offset + 1], whole[offset + 2]];
    if (Number(minutes) >= 60 || Number(seconds) >= 60) return null;
    return { value: Number(degrees) + Number(minutes) / 60 + Number(seconds) / 3600, hemisphere: hemisphere?.toUpperCase() };
  };
  const a = read(1);
  const b = read(5);
  if (!a || !b) return null;
  const isLat = (h?: string) => h === 'N' || h === 'S';
  const isLng = (h?: string) => h === 'E' || h === 'W';
  let lat = a, lng = b;
  if (isLng(a.hemisphere) && isLat(b.hemisphere)) [lat, lng] = [b, a];
  else if ((a.hemisphere && !isLat(a.hemisphere)) || (b.hemisphere && !isLng(b.hemisphere))) return null;
  const latitude = lat.hemisphere === 'S' ? -lat.value : lat.value;
  const longitude = lng.hemisphere === 'W' ? -lng.value : lng.value;
  return validPair(latitude, longitude) ? [latitude, longitude] : null;
}

/** "24.7136 N, 46.6753 E", "24.7136, 46.6753", "24.7136 46.6753". */
function parseDecimal(text: string): [number, number] | null {
  const m = new RegExp(String.raw`^\s*(${NUM})\s*°?\s*([NS])?\s*(?:,|;|\s)\s*(${NUM})\s*°?\s*([EW])?\s*$`, 'i').exec(text);
  if (!m) return null;
  let latitude = Number(m[1]);
  let longitude = Number(m[3]);
  if (m[2]?.toUpperCase() === 'S') latitude = -Math.abs(latitude);
  if (m[4]?.toUpperCase() === 'W') longitude = -Math.abs(longitude);
  return validPair(latitude, longitude) ? [latitude, longitude] : null;
}

export function parseLocationInput(raw: string): ParsedLocation {
  const text = normalizeDigits(raw ?? '').trim();
  if (!text || text.length > 2048) return { kind: 'unreadable' };
  if (/^(https?:\/\/|www\.|maps\.|goo\.gl\/|google\.)/i.test(text) || /^[a-z0-9.-]+\.[a-z]{2,}\/\S*/i.test(text)) {
    return parseUrl(text) ?? { kind: 'unreadable' };
  }
  return asCoordinates(parseDecimal(text) ?? parseDms(text), 'text') ?? { kind: 'unreadable' };
}
