import client from './client';

export interface MaintenanceStatus {
  on: boolean;
  message: string | null;
}

/**
 * Maintenance mode (Organisation Admin): while it is on, every other API call answers 503 with the message. These
 * routes stay reachable, so the administrator who switched it on can switch it off.
 */
export const maintenanceApi = {
  get: () => client.get<MaintenanceStatus>('/api/organisation-admin/maintenance').then(r => r.data),
  set: (on: boolean, message?: string) =>
    client.put<MaintenanceStatus>('/api/organisation-admin/maintenance', { on, message: message || null }).then(r => r.data),
};
