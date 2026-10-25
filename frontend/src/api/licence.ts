import client from './client';

export type LicenceState = 'Missing' | 'Valid' | 'Grace' | 'Expired';

export interface LicenceView {
  state: LicenceState;
  installationId: string;
  licenceId: string | null;
  licensee: string | null;
  edition: string | null;
  accountType: string;
  issuedAtUtc: string | null;
  expiresAtUtc: string | null;
  graceDays: number | null;
  supportExpiresAtUtc: string | null;
  limits: { maxEmployees: number; maxUsers: number; maxCompanies: number; maxAdminUsers: number; aiMonthlyTokenLimit: number };
  usage: { activeEmployees: number; activeUsers: number; companies: number };
  modules: string[];
  optInFeatures: string[];
  countries: string[] | null;
  version: string;
}

export interface LicenceImportResult {
  outcome: 'Imported' | 'AlreadyActive';
  state: LicenceState;
  licenceId: string;
  licensee: string;
}

/** The installation's licence (Organisation Admin → Licence). Reachable without a valid licence. */
export const licenceApi = {
  get: () => client.get<LicenceView>('/api/organisation-admin/licence').then(r => r.data),

  import: (file: File) => {
    const form = new FormData();
    form.append('file', file);
    return client.post<LicenceImportResult>('/api/organisation-admin/licence', form).then(r => r.data);
  },
};
