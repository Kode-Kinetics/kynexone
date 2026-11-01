import type { CompanyDto } from '../api/organization';
import type { SaudiBankExportSettings } from '../api/saudiBankExports';

/** Setup > Bank and WPS: the decisions behind the panel, kept pure so every state is unit-tested. */

const SAUDI_CODES = new Set(['SA', 'SAU', 'KSA']);
export const isSaudiCountry = (code: string | null | undefined): boolean => SAUDI_CODES.has((code ?? '').trim().toUpperCase());

export type BankSettingsScope =
  | { kind: 'noSaudi' }
  /** Setup is scoped to a company that is not Saudi, so nothing here applies to it (its WPS employer id is on the company). */
  | { kind: 'scopeNotSaudi'; scoped: CompanyDto }
  | { kind: 'choose'; options: CompanyDto[] }
  | { kind: 'selected'; company: CompanyDto; showPicker: boolean; options: CompanyDto[] };

export function resolveBankSettingsScope({ companyId, companies, picked }: {
  companyId: string | null | undefined; companies: readonly CompanyDto[] | undefined; picked: string;
}): BankSettingsScope {
  const all = companies ?? [];
  const options = all.filter(c => isSaudiCountry(c.countryCode));
  if (companyId) {
    const scoped = all.find(c => c.id === companyId);
    if (scoped && isSaudiCountry(scoped.countryCode)) return { kind: 'selected', company: scoped, showPicker: false, options };
    if (scoped) return { kind: 'scopeNotSaudi', scoped };
    // Roles that may run payroll but not read the company list (Payroll Manager) get an empty list. The company Setup is
    // scoped to is still theirs to configure; the API authorises the company, so use it rather than claiming none exists.
    if (all.length === 0) return { kind: 'selected', company: { id: companyId, legalNameEn: '', tradeName: '', countryCode: 'SA' } as CompanyDto, showPicker: false, options };
  }
  if (options.length === 0) return { kind: 'noSaudi' };
  if (options.length === 1) return { kind: 'selected', company: options[0], showPicker: false, options };
  const chosen = options.find(c => c.id === picked);
  return chosen ? { kind: 'selected', company: chosen, showPicker: true, options } : { kind: 'choose', options };
}

/**
 * What the form is compared against to know it has unsaved edits. The only supported format is pre-selected for a company
 * that has saved nothing yet; that is a default the screen offers, not something the user edited, so it is part of the
 * baseline (otherwise an untouched form asks "leave without saving?").
 */
export function bankSettingsBaseline(saved: SaudiBankExportSettings, formats: readonly { id: string }[]): SaudiBankExportSettings {
  return !saved.formatId && formats.length === 1 ? { ...saved, formatId: formats[0].id } : saved;
}

export type BankSettingsLoadProblem = { kind: 'forbidden' | 'notFound' | 'failed'; retry: boolean };
export function bankSettingsLoadProblem(status: number | undefined): BankSettingsLoadProblem {
  if (status === 403) return { kind: 'forbidden', retry: false };
  if (status === 404) return { kind: 'notFound', retry: false };
  return { kind: 'failed', retry: true };
}
