import client from './client';

export interface GlAccount {
  id: string;
  code: string;
  name: string;
  accountType: string; // Asset | Liability | Expense | Equity | Revenue
  isActive: boolean;
}

export interface GlAccountRequest {
  code: string;
  name: string;
  accountType: string;
  isActive: boolean;
}

export interface GlMappingRow {
  driverKey: string;
  label: string;
  accountType: string;
  defaultAccount: string;
  mappedAccountId: string | null;
}

export const financeGlApi = {
  listAccounts: () => client.get<GlAccount[]>('/api/finance/gl/accounts').then((r) => r.data),
  createAccount: (data: GlAccountRequest) => client.post<GlAccount>('/api/finance/gl/accounts', data).then((r) => r.data),
  updateAccount: (id: string, data: GlAccountRequest) => client.put<GlAccount>(`/api/finance/gl/accounts/${id}`, data).then((r) => r.data),
  deleteAccount: (id: string) => client.delete(`/api/finance/gl/accounts/${id}`),
  listMappings: () => client.get<GlMappingRow[]>('/api/finance/gl/mappings').then((r) => r.data),
  setMappings: (mappings: { driverKey: string; accountId: string }[]) =>
    client.put<{ count: number }>('/api/finance/gl/mappings', mappings).then((r) => r.data),
  seedDefaults: () => client.post<{ accounts: number; mappings: number }>('/api/finance/gl/seed-defaults', {}).then((r) => r.data),
};
