import { useTranslation } from 'react-i18next';
import { useFormat } from '@/utils/format';
import type { Payslip } from '@/types';
import { monthBounds, periodIso } from './payslipLogic';

/** Month label in the UI language, from the payslip's own year/month; the server's label otherwise. */
export function usePayslipPeriod() {
  const f = useFormat();
  return (p: Pick<Payslip, 'year' | 'month' | 'periodLabel'>) => {
    const iso = periodIso(p.year, p.month);
    return iso ? f.date(iso, 'monthYear') : p.periodLabel;
  };
}

/** Hijri months the pay month spans ("Rabiʻ I – Rabiʻ II 1448"); '' when the runtime can't render Hijri. */
export function useHijriPeriod() {
  const f = useFormat();
  const { t } = useTranslation();
  return (year: number, month: number) => {
    const b = monthBounds(year, month);
    if (!b) return '';
    const first = f.hijri(b.first, 'monthYear');
    const last = f.hijri(b.last, 'monthYear');
    if (!first || !last) return '';
    return first === last ? first : t('leavePay.common.dateRange', { start: first, end: last });
  };
}
