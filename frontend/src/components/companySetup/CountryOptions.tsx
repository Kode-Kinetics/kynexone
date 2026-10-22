'use client';

import { useMemo } from 'react';
import { useLocale } from '../../contexts/LocaleContext';
import { countryDisplayName } from '../../lib/companySetupWizard';

/** Countries a company can be registered in from this wizard; names come from the browser in the viewer's language. */
export const WIZARD_COUNTRIES = ['SA', 'AE', 'QA', 'KW', 'BH', 'OM', 'EG', 'IN', 'PK', 'GB', 'US'] as const;

export function useCountryName() {
  const { locale } = useLocale();
  return useMemo(() => {
    let names: Intl.DisplayNames | null = null;
    try { names = new Intl.DisplayNames([locale], { type: 'region' }); } catch { names = null; }
    return (code: string | null | undefined) => countryDisplayName(names, code);
  }, [locale]);
}

export function CountryOptions({ includeEmpty = true }: { includeEmpty?: boolean }) {
  const { t } = useLocale();
  const name = useCountryName();
  return (
    <>
      {includeEmpty && <option value="">{t('Choose a country…')}</option>}
      {WIZARD_COUNTRIES.map(code => <option key={code} value={code}>{name(code)}</option>)}
    </>
  );
}
