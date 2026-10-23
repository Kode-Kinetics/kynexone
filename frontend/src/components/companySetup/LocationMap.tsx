'use client';

import { useEffect, useRef, useState } from 'react';
import type * as Leaflet from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { useT } from '../../hooks/useT';
import { roundCoordinate } from '../../lib/mapLink';

/**
 * A live OpenStreetMap preview of one location: the pin and its geofence circle. Drag the pin, or click the map, to
 * move it. Leaflet is loaded on first use (it touches `window`), and the standard OSM tiles carry their attribution.
 * No key, no vendor account. Before anything is pinned the map opens on `fallback` with no pin: nothing is invented.
 */
export function LocationMap({ latitude, longitude, radius, fallback, readOnly, label, onMove }: {
  latitude: number | null;
  longitude: number | null;
  radius: number;
  fallback: [number, number];
  readOnly?: boolean;
  /** Accessible name, e.g. "Map of Main gate". */
  label: string;
  onMove: (latitude: number, longitude: number) => void;
}) {
  const t = useT();
  const host = useRef<HTMLDivElement>(null);
  const L = useRef<typeof Leaflet | null>(null);
  const map = useRef<Leaflet.Map | null>(null);
  const marker = useRef<Leaflet.Marker | null>(null);
  const circle = useRef<Leaflet.Circle | null>(null);
  const moveRef = useRef(onMove);
  moveRef.current = onMove;
  const readOnlyRef = useRef(readOnly);
  readOnlyRef.current = readOnly;
  /** The last position the map itself reported, so its own move does not re-frame the view. */
  const own = useRef<string | null>(null);
  const [state, setState] = useState<'loading' | 'ready' | 'failed'>('loading');
  const pinned = latitude != null && longitude != null;
  const attribution = t('© OpenStreetMap contributors');

  useEffect(() => {
    let disposed = false;
    let observer: ResizeObserver | null = null;
    import('leaflet').then(mod => {
      if (disposed || !host.current) return;
      const lib = ((mod as unknown as { default?: typeof Leaflet }).default ?? mod) as typeof Leaflet;
      L.current = lib;
      const m = lib.map(host.current, { scrollWheelZoom: false, zoomControl: true, attributionControl: true });
      m.attributionControl.setPrefix('<a href="https://leafletjs.com" target="_blank" rel="noopener noreferrer">Leaflet</a>');
      lib.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: `<a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener noreferrer">${attribution}</a>`,
      }).addTo(m);
      m.setView(fallback, 11);
      m.on('click', event => {
        if (readOnlyRef.current) return;
        const next = [roundCoordinate(event.latlng.lat), roundCoordinate(event.latlng.lng)] as const;
        own.current = next.join(',');
        moveRef.current(next[0], next[1]);
      });
      map.current = m;
      observer = new ResizeObserver(() => m.invalidateSize());
      observer.observe(host.current);
      setState('ready');
    }).catch(() => { if (!disposed) setState('failed'); });
    return () => {
      disposed = true;
      observer?.disconnect();
      map.current?.remove();
      map.current = null; marker.current = null; circle.current = null;
    };
    // The map is made once; position, radius and mode are applied below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const lib = L.current;
    const m = map.current;
    if (state !== 'ready' || !lib || !m) return;
    if (!pinned) {
      marker.current?.remove(); marker.current = null;
      circle.current?.remove(); circle.current = null;
      return;
    }
    const at: [number, number] = [latitude!, longitude!];
    if (!marker.current) {
      marker.current = lib.marker(at, {
        draggable: !readOnly, keyboard: true, title: label, alt: label, autoPan: true,
        icon: lib.divIcon({
          className: 'kx-map-pin', iconSize: [30, 40], iconAnchor: [15, 40],
          html: '<svg width="30" height="40" viewBox="0 0 30 40" aria-hidden="true"><path d="M15 39C15 39 28 24.5 28 14.5A13 13 0 0 0 2 14.5C2 24.5 15 39 15 39Z" fill="#2F6BFF" stroke="#fff" stroke-width="2.5"/><circle cx="15" cy="14.5" r="5" fill="#fff"/></svg>',
        }),
      }).addTo(m);
      marker.current.on('dragend', () => {
        const p = marker.current!.getLatLng();
        const next = [roundCoordinate(p.lat), roundCoordinate(p.lng)] as const;
        own.current = next.join(',');
        moveRef.current(next[0], next[1]);
      });
      circle.current = lib.circle(at, { radius, color: '#2F6BFF', weight: 2, fillColor: '#2F6BFF', fillOpacity: 0.12 }).addTo(m);
    } else {
      marker.current.setLatLng(at);
      circle.current?.setLatLng(at);
    }
    circle.current?.setRadius(radius);
    if (readOnly) marker.current.dragging?.disable(); else marker.current.dragging?.enable();
    const key = `${latitude},${longitude}`;
    // Frame the geofence when the pin came from outside (a link, coordinates, the device); not after a drag.
    if (own.current !== key) m.fitBounds(circle.current!.getBounds(), { maxZoom: 17, padding: [24, 24], animate: false });
    own.current = key;
  }, [state, pinned, latitude, longitude, radius, readOnly, label]);

  return (
    <div className="relative isolate h-60 overflow-hidden rounded-lg border border-slate-200 bg-slate-100 dark:border-white/10 dark:bg-slate-800 sm:h-72">
      {/* Leaflet lays its panes out left-to-right; the page direction must not mirror the tiles. */}
      <div ref={host} dir="ltr" role="region" aria-label={label} className="kx-location-map absolute inset-0 z-0" data-testid="location-map" data-ready={state === 'ready' ? 'true' : undefined} />
      {state !== 'ready' && (
        <p className="absolute inset-0 grid place-items-center px-4 text-center text-sm text-slate-600 dark:text-slate-300" role="status">
          {state === 'loading' ? t('Loading the map…') : t('The map could not load. You can still paste a link or the coordinates.')}
        </p>
      )}
    </div>
  );
}
