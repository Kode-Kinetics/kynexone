import client from './client';

/**
 * Reads the coordinates behind a Google Maps short link (maps.app.goo.gl/…). The backend contacts only Google's
 * short-link and Maps hosts and returns nothing but the latitude and longitude; a link it cannot read is a 422
 * ERR_MAP_LINK_UNREADABLE. Full links and plain coordinates are parsed in the browser (lib/mapLink.ts).
 */
export const geoApi = {
  resolveMapLink: (url: string) =>
    client.get<{ latitude: number; longitude: number }>('/api/geo/resolve-map-link', { params: { url } }).then(r => r.data),
};
