/**
 * Which currency a payroll amount is in.
 *
 * A payroll run is denominated in its legal entity's currency (Company.DefaultCurrency) — the same
 * currency the server posts the GL journal and builds the bank file in. The tenant's display default
 * is NOT a substitute: a Saudi company's run was shown to its approver as "USD 4,284,600.00" because
 * the tenant default had been left at USD. When the company currency cannot be established the
 * amount is shown without one and approval is withheld, rather than labelled with a guess.
 */

export type RunCurrencyResolution =
  | { status: 'resolved'; currency: string; companyName?: string }
  | { status: 'loading' }
  | { status: 'unavailable'; reason: string };

export type CompaniesLoadState = 'loading' | 'loaded' | 'failed';

export interface CurrencyCompany {
  id: string;
  name?: string;
  tradeName?: string;
  defaultCurrency?: string | null;
}

const normalize = (currency: string | null | undefined) => (currency ?? '').trim().toUpperCase();
const companyLabel = (company: CurrencyCompany) => company.tradeName?.trim() || company.name?.trim() || undefined;

export function resolvePayrollRunCurrency(
  run: { companyId: string | null } | null | undefined,
  companies: CurrencyCompany[],
  companiesState: CompaniesLoadState,
): RunCurrencyResolution {
  if (!run) return { status: 'unavailable', reason: 'No payroll run is selected.' };
  if (companiesState === 'loading') return { status: 'loading' };
  if (companiesState === 'failed') {
    return {
      status: 'unavailable',
      reason: "The employing company's currency could not be loaded, so these amounts are shown without a currency.",
    };
  }

  if (run.companyId) {
    const company = companies.find((c) => c.id === run.companyId);
    if (!company) {
      return {
        status: 'unavailable',
        reason: "This run's legal entity is not in the active company list, so its currency cannot be confirmed. Check Setup → Companies before approving.",
      };
    }
    const currency = normalize(company.defaultCurrency);
    if (!currency) {
      return {
        status: 'unavailable',
        reason: `${companyLabel(company) ?? 'The employing company'} has no default currency set. Set it in Setup → Companies before approving.`,
      };
    }
    return { status: 'resolved', currency, companyName: companyLabel(company) };
  }

  // A run without a legal entity predates entity scoping. Its amounts are only in one currency if
  // every active company pays in the same one; otherwise no single label is honest.
  const currencies = distinctCurrencies(companies);
  if (currencies.length === 1) return { status: 'resolved', currency: currencies[0] };
  return {
    status: 'unavailable',
    reason: currencies.length > 1
      ? `This run has no legal entity and your companies pay in ${currencies.join(', ')}, so its totals cannot be shown in one currency. Assign it to a company before approving.`
      : 'This run has no legal entity and no company currency is configured. Set one in Setup → Companies before approving.',
  };
}

function distinctCurrencies(companies: Array<{ defaultCurrency?: string | null }>): string[] {
  return [...new Set(companies.map((c) => normalize(c.defaultCurrency)).filter(Boolean))].sort();
}

/**
 * The single currency shared by every company in a group total, or null when they differ. Summing
 * SAR and AED into one figure produces a number that is not an amount in either currency, so a
 * mixed group must be read per company instead.
 */
export function commonPayrollCurrency(
  companies: Array<{ currency?: string | null; hasPayrollRun?: boolean }>,
): string | null {
  const currencies = new Set(
    companies
      .filter((company) => company.hasPayrollRun !== false)
      .map((company) => normalize(company.currency))
      .filter(Boolean),
  );
  return currencies.size === 1 ? [...currencies][0] : null;
}
