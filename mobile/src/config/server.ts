// ============================================================
// KynexOne server — the installation this app talks to
// ============================================================
//
// Chosen at runtime (typed or scanned on the server screen), checked against GET /api/public/installation and kept
// in secure storage. A development build may still point at EXPO_PUBLIC_API_BASE_URL; a store build has no server
// until someone chooses one.

import { secureStorage } from '@/storage';
import { APP_CONFIG } from '@/config';
import { apiBaseUrlFor, isInstallationInfo, normalizeServerAddress, type InstallationInfo } from './serverAddress';

const SERVER_KEY = 'kynexone_server';
const CHECK_TIMEOUT_MS = 10_000;

export interface SavedServer {
  origin: string;
  organisationName: string | null;
  pushEnabled: boolean;
}

let current: SavedServer | null = null;

/** Development builds may use http:// and a LAN or local server; store builds may not. */
export const allowInsecureServer = typeof __DEV__ !== 'undefined' && __DEV__;

/** Reads the saved server into memory. Call once at start, before any API client is created. */
export async function loadServer(): Promise<SavedServer | null> {
  const raw = await secureStorage.get(SERVER_KEY);
  if (!raw) return (current = null);
  try {
    const saved = JSON.parse(raw) as SavedServer;
    const check = normalizeServerAddress(saved.origin, { allowInsecure: allowInsecureServer });
    current = check.ok ? { ...saved, origin: check.origin } : null;
  } catch {
    current = null;
  }
  return current;
}

export function getServer(): SavedServer | null {
  return current;
}

/** The API base every client uses: the chosen server, else a development build's configured URL, else ''. */
export function getApiBaseUrl(): string {
  if (current) return apiBaseUrlFor(current.origin);
  return APP_CONFIG.API_BASE_URL;
}

/** Whether there is a server to sign in to. */
export function hasServer(): boolean {
  return !!getApiBaseUrl();
}

/** Push notifications are off unless the chosen server says they are on (they leave the network through Expo). */
export function isPushEnabled(): boolean {
  return current?.pushEnabled ?? false;
}

export class ServerCheckError extends Error {
  constructor(public readonly reason: 'unreachable' | 'notKynexOne' | 'notSetUp') {
    super(reason);
  }
}

/** Asks a server what it is. Refuses anything that is not a set-up KynexOne installation. */
export async function checkServer(origin: string): Promise<InstallationInfo> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), CHECK_TIMEOUT_MS);
  let body: unknown;
  try {
    const response = await fetch(`${apiBaseUrlFor(origin)}/public/installation`, {
      headers: { Accept: 'application/json' },
      signal: controller.signal,
    });
    if (!response.ok) throw new ServerCheckError('notKynexOne');
    body = await response.json();
  } catch (error) {
    if (error instanceof ServerCheckError) throw error;
    if (error instanceof SyntaxError) throw new ServerCheckError('notKynexOne');
    throw new ServerCheckError('unreachable');
  } finally {
    clearTimeout(timer);
  }
  if (!isInstallationInfo(body)) throw new ServerCheckError('notKynexOne');
  if (!body.setupComplete) throw new ServerCheckError('notSetUp');
  return body;
}

export async function saveServer(origin: string, info: InstallationInfo): Promise<SavedServer> {
  const saved: SavedServer = { origin, organisationName: info.organisationName, pushEnabled: info.pushEnabled };
  await secureStorage.set(SERVER_KEY, JSON.stringify(saved));
  current = saved;
  return saved;
}

/** Re-reads the server's answer (push may have been switched since) without changing the address. */
export async function refreshServer(): Promise<void> {
  if (!current) return;
  try {
    await saveServer(current.origin, await checkServer(current.origin));
  } catch {
    // Offline or in maintenance: keep what is saved.
  }
}

export async function forgetServer(): Promise<void> {
  await secureStorage.delete(SERVER_KEY);
  current = null;
}
