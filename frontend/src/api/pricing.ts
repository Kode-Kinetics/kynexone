import axios from 'axios';
import { BASE_URL } from './client';

const api = axios.create({ baseURL: BASE_URL });

// ── Types ─────────────────────────────────────────────────────────────────────

export type OrgType = 'single' | 'group' | 'enterprise_holding';

export interface EstimateRequest {
  orgType: OrgType;
  numCompanies: number;
  numBranches: number;
  numEmployees: number;
  numAdminUsers: number;
  numCountries: number;
  needsArabic: boolean;
  selectedModules: string[];
}

export interface ModuleAddon {
  key: string;
  name: string;
  monthlyPrice: number;
  isIncluded: boolean;
  isEnterpriseOnly: boolean;
}

export interface EstimateBreakdown {
  basePlanPrice: number;
  includedEmployees: number | null;
  includedCompanies: number | null;
  includedAdminUsers: number | null;
  extraEmployeeCount: number;
  extraEmployeeCharge: number;
  extraCompanyCount: number;
  extraCompanyCharge: number;
  extraAdminUserCount: number;
  extraAdminUserCharge: number;
  arabicSupplement: number;
  extraCountryCharge: number;
  moduleAddOns: ModuleAddon[];
  totalAddOnCharge: number;
  implementationEstimate: number;
}

export interface PricingEstimate {
  recommendedPlan: string;
  breakdown: EstimateBreakdown;
  includedFeatures: string[];
  paidAddOns: string[];
  monthlyTotal: number;
  annualTotal: number;
  annualDiscountPct: number;
  isEnterpriseRequired: boolean;
  disclaimer: string;
}

export interface PricingModule {
  key: string;
  name: string;
  includedInTrial: boolean;
  includedInStarter: boolean;
  includedInGrowth: boolean;
  includedInEnterprise: boolean;
  isEnterpriseOnly: boolean;
  addonPriceMonthly: number;
}

export interface QuoteRequest {
  companyName: string;
  contactName: string;
  contactEmail: string;
  phone?: string;
  orgType: OrgType;
  numCompanies: number;
  numBranches: number;
  numEmployees: number;
  numAdminUsers: number;
  numCountries: number;
  needsArabic: boolean;
  selectedModules: string[];
  estimatedMonthlyAmount: number;
  estimatedAnnualAmount: number;
  notes?: string;
}

export interface PricingQuote {
  id: string;
  companyName: string;
  contactName: string;
  contactEmail: string;
  phone?: string;
  orgType: string;
  numCompanies: number;
  numBranches: number;
  numEmployees: number;
  numAdminUsers: number;
  numCountries: number;
  needsArabic: boolean;
  selectedModulesJson: string;
  estimatedMonthlyAmount: number;
  estimatedAnnualAmount: number;
  notes?: string;
  status: string;
  convertedToTenantId?: string;
  createdAtUtc: string;
}

export interface TenantSubscriptionUsage {
  plan: string;
  status: string;
  billingCycle: string;
  monthlyAmount: number;
  currencyCode: string;
  expiresAtUtc?: string;
  limits: {
    maxEmployees: number;
    maxUsers: number;
    maxCompanies: number;
    maxAdminUsers: number;
  };
  usage: {
    activeEmployees: number;
    totalUsers: number;
    totalCompanies: number;
    aiTokensThisMonth: number;
  };
  featureFlags: Record<string, boolean>;
}

// ── API ───────────────────────────────────────────────────────────────────────

export const pricingApi = {
  getModules: (): Promise<PricingModule[]> =>
    api.get('/api/pricing/modules').then(r => r.data),

  estimate: (req: EstimateRequest): Promise<PricingEstimate> =>
    api.post('/api/pricing/estimate', req).then(r => r.data),

  submitQuote: (req: QuoteRequest): Promise<{ id: string; message: string }> =>
    api.post('/api/pricing/quotes', req).then(r => r.data),
};
