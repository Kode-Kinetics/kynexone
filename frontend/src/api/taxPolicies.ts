import client from './client';

// Per-company, client-configurable income-tax policy (opt-in). Drives both monthly
// payroll income tax and bonus withholding. Mirrors the backend CompanyTaxPolicy.
export interface TaxPolicy {
  companyId: string;
  companyName: string;
  countryCode: string;
  isEnabled: boolean;
  taxMode: 'None' | 'Flat';
  flatRatePercent: number;
  appliesToSalary: boolean;
  appliesToBonus: boolean;
  stateOrRegion: string;
  notes: string;
}

export interface UpsertTaxPolicyRequest {
  isEnabled: boolean;
  taxMode: 'None' | 'Flat';
  flatRatePercent: number;
  appliesToSalary: boolean;
  appliesToBonus: boolean;
  stateOrRegion?: string;
  notes?: string;
}

export const taxPoliciesApi = {
  list: () => client.get<TaxPolicy[]>('/api/tax-policies').then((r) => r.data),
  get: (companyId: string) =>
    client.get<TaxPolicy>(`/api/tax-policies/company/${companyId}`).then((r) => r.data),
  upsert: (companyId: string, body: UpsertTaxPolicyRequest) =>
    client.put<TaxPolicy>(`/api/tax-policies/company/${companyId}`, body).then((r) => r.data),
};
