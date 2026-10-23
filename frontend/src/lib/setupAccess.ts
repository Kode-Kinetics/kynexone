import type { CompanyAccess } from '../api/auth';
import type { CompanyDto } from '../api/organization';

/**
 * Any one of these opens Setup → GL & rates. The panel itself shows only the sub-sections the caller can use
 * (GlSetupPanel's permission map), so a Finance user with finance.gl.read sees the accounts and nothing they cannot open.
 */
export const GL_AND_RATES_PERMISSIONS = [
  'finance.gl.read', 'finance.gl.manage', 'finance.gl.drivers.manage',
  'payroll.rates.read', 'payroll.rates.manage', 'payroll.rates.statutory_override',
] as const;

/**
 * GET /api/companies is role-gated on the server (CompaniesController: Admin, HR Manager, HR Officer, Auditor).
 * organization.read alone gets a 403, which used to leave a Payroll Manager or a Recruiter on an empty Companies page.
 */
export const COMPANY_LIST_ROLES = ['Admin', 'HR Manager', 'HR Officer', 'Auditor'] as const;

export interface SetupAccessCheck {
  hasPermission: (permission: string) => boolean;
  hasRole: (role: string) => boolean;
}

export function canListCompanies({ hasRole }: SetupAccessCheck): boolean {
  return COMPANY_LIST_ROLES.some(role => hasRole(role));
}

export function canOpenGlAndRates({ hasPermission }: SetupAccessCheck): boolean {
  return GL_AND_RATES_PERMISSIONS.some(permission => hasPermission(permission));
}

/**
 * The companies GL & rates offers in its scope selector. The full company list when the caller may read it (the
 * server already narrows it to the switcher's company); otherwise the active companies the caller's own sign-in
 * grants, narrowed to the company chosen in the company switcher when there is one: every request carries that
 * company (X-Company-Id) and the server refuses any other. Fields the list would have added are left blank:
 * GL & rates reads only the id, names, country, jurisdiction and currency.
 */
export function glCompanies(listed: CompanyDto[], authorized: CompanyAccess[], selectedCompanyId: string | null = null): CompanyDto[] {
  if (listed.length > 0) return listed;
  return authorized.filter(company => company.isActive && (selectedCompanyId === null || company.id === selectedCompanyId)).map(company => ({
    id: company.id, legalNameEn: company.name, legalNameAr: '', tradeName: '', countryCode: company.countryCode, jurisdiction: '',
    registrationNumber: '', taxNumber: '', wpsEmployerId: '', gosiEmployerId: '', qiwaEstablishmentId: '', defaultCurrency: '',
    emailDomain: '', workEmailPattern: '', isActive: true, approvalStatus: 'Active',
  }));
}
