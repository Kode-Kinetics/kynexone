import client from './client';

// Company governance: per-legal-entity tax policies and compliance readiness
// profiles. CONFIGURABLE FOUNDATION ONLY — never legal, tax, or statutory advice.

export interface CompanyTaxPolicy {
  id: string;
  companyId: string | null;
  countryCode: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  status: 'Draft' | 'Active' | 'Archived';
  incomeTaxRatePercent: number | null;
  appliesToBonus: boolean;
  notes: string;
}

export interface CompanyTaxPolicyInput {
  companyId: string | null;
  countryCode: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  status?: string;
  incomeTaxRatePercent?: number | null;
  appliesToBonus?: boolean;
  notes?: string;
}

export interface CompanyComplianceProfile {
  id: string;
  companyId: string | null;
  countryCode: string;
  jurisdiction: string;
  compliancePack: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  status: string;
  requiredFieldsJson: string;
  notes: string;
}

export interface ComplianceFieldReadiness {
  field: string;
  failClosed: boolean;
  missingEmployeeCount: number;
}

export interface ComplianceReadiness {
  profile: CompanyComplianceProfile | null;
  totalEmployees: number;
  requiredFields: ComplianceFieldReadiness[];
  disclaimer: string;
}

export const taxPoliciesApi = {
  list: (companyId?: string) =>
    client.get<CompanyTaxPolicy[]>('/api/company-tax-policies', { params: { companyId } }).then((r) => r.data),
  create: (input: CompanyTaxPolicyInput) =>
    client.post<CompanyTaxPolicy>('/api/company-tax-policies', input).then((r) => r.data),
  update: (id: string, input: CompanyTaxPolicyInput) =>
    client.put<CompanyTaxPolicy>(`/api/company-tax-policies/${id}`, input).then((r) => r.data),
  remove: (id: string) => client.delete(`/api/company-tax-policies/${id}`),
};

export const complianceProfilesApi = {
  list: (companyId?: string) =>
    client.get<CompanyComplianceProfile[]>('/api/company-compliance-profiles', { params: { companyId } }).then((r) => r.data),
  readiness: (companyId: string) =>
    client.get<ComplianceReadiness>('/api/company-compliance-profiles/readiness', { params: { companyId } }).then((r) => r.data),
  create: (input: Partial<CompanyComplianceProfile>) =>
    client.post<CompanyComplianceProfile>('/api/company-compliance-profiles', input).then((r) => r.data),
  update: (id: string, input: Partial<CompanyComplianceProfile>) =>
    client.put<CompanyComplianceProfile>(`/api/company-compliance-profiles/${id}`, input).then((r) => r.data),
};
