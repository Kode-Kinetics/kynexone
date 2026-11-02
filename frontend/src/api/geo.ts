import client from './client';

/**
 * Reads the coordinates behind a Google Maps short link (maps.app.goo.gl/…). The backend contacts only Google's
 * short-link and Maps hosts and returns nothing but the latitude and longitude; a link it cannot read is a 422
 * ERR_MAP_LINK_UNREADABLE. Full links and plain coordinates are parsed in the browser (lib/mapLink.ts).
 */
export interface AddressSuggestion { label: string; name?: string | null; city?: string | null; countryCode?: string | null; latitude: number; longitude: number }

export const geoApi = {
  /** Up to five candidate places for a typed address; an empty list when nothing matched or the lookup was unavailable. */
  searchAddress: (q: string, country: string | undefined, lang: string) =>
    client.get<AddressSuggestion[]>('/api/geo/search-address', { params: { q, country, lang } }).then(r => r.data),
  resolveMapLink: (url: string) =>
    client.get<{ latitude: number; longitude: number }>('/api/geo/resolve-map-link', { params: { url } }).then(r => r.data),
};
