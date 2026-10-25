'use client';

import { useEffect, useState } from 'react';
import client from './client';

/** GET /api/public/installation — what anyone may know about this installation before signing in. */
export interface PublicInstallationInfo {
  setupComplete: boolean;
  organisationName: string | null;
  publicBaseUrl: string;
  deviceIngestUrl: string;
  pushEnabled: boolean;
  licence: { state: 'Missing' | 'Valid' | 'Grace' | 'Expired' };
}

let cached: Promise<PublicInstallationInfo> | null = null;

/** Read once per page load and shared: the answer does not change while the page is open. */
export function getPublicInstallation(): Promise<PublicInstallationInfo> {
  cached ??= client.get<PublicInstallationInfo>('/api/public/installation').then(r => r.data)
    .catch(err => { cached = null; throw err; });
  return cached;
}

/**
 * The public address attendance devices and KynexBridge post to. Never the API's internal container address
 * (`http://backend:5117`): the server states its public APP_URL; before that answer (or if it fails), the address
 * this browser reached the product on.
 */
export function useDeviceIngestUrl(): string {
  const fallback = typeof window === 'undefined' ? '/api/attendance/ingest' : `${window.location.origin}/api/attendance/ingest`;
  const [url, setUrl] = useState(fallback);
  useEffect(() => {
    let live = true;
    getPublicInstallation()
      .then(info => { if (live && info.deviceIngestUrl.startsWith('http')) setUrl(info.deviceIngestUrl); })
      .catch(() => { /* keep the fallback */ });
    return () => { live = false; };
  }, []);
  return url;
}
