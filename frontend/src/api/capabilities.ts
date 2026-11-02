import client from './client';

/**
 * What this build can do, feature by feature (GET /api/capabilities). A form whose save the server refuses with 501 is
 * hidden behind its capability, with a short note, rather than offered and then rejected.
 */
export type Capabilities = Record<string, boolean>;

let pending: Promise<Capabilities> | null = null;

/** Fetched once per page load; a failed fetch is retried next time, and meanwhile nothing is hidden (the server still refuses). */
export function loadCapabilities(): Promise<Capabilities> {
  if (!pending) {
    pending = client.get<{ features: Capabilities }>('/api/capabilities').then(r => r.data.features ?? {}).catch(() => { pending = null; return {} as Capabilities; });
  }
  return pending;
}

/** A capability is off only when the server says so; an unknown or unloaded one counts as on. */
export const isCapabilityOff = (capabilities: Capabilities | null, key: string): boolean => capabilities !== null && capabilities[key] === false;
