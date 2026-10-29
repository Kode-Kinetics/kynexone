'use client';

import { useEffect, useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { belowMinimumApi, type BelowMinimumItem } from '../api/setup';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { msg } from '../i18n/translations';

const SOURCE_LABEL: Record<BelowMinimumItem['source'], string> = {
  leave_policy: msg('Leave policy'), eosb: msg('End of service'), payroll_rule: msg('Payroll rule'),
};

/**
 * "Rules below the legal minimum": every configured rule that sits under a statutory minimum, whether or not an administrator
 * acknowledged it, so nothing is hidden (owner decision 2026-10-25). Read-only; the rules are edited where they live.
 */
export function BelowMinimumRules() {
  const { t, locale } = useLocale();
  const format = useFormat();
  const [items, setItems] = useState<BelowMinimumItem[] | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let live = true;
    belowMinimumApi.list().then(r => { if (live) setItems(Array.isArray(r) ? r : []); }).catch(() => { if (live) setFailed(true); });
    return () => { live = false; };
  }, []);
  if (failed) return null; // a caller without the HR roles simply does not get the list
  if (items === null) return null;
  return (
    <section data-testid="below-minimum-rules" className="rounded-xl border border-slate-200 bg-white p-4 dark:border-white/10 dark:bg-white/[0.03]">
      <div className="flex items-start gap-3">
        <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-600" aria-hidden="true" />
        <div>
          <h3 className="text-sm font-semibold text-slate-950 dark:text-white">{t('Rules below the legal minimum')}</h3>
          <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{t('Every rule set below what the law requires, and who accepted it. Nothing is hidden.')}</p>
        </div>
      </div>
      {items.length === 0 ? (
        <p className="mt-3 text-sm text-slate-500">{t('No rules are below the legal minimum.')}</p>
      ) : (
        <div className="mt-3 overflow-x-auto">
          <table className="w-full text-start text-sm">
            <thead className="text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="py-2 pe-3 text-start font-semibold">{t('Company')}</th>
                <th className="py-2 pe-3 text-start font-semibold">{t('Rule')}</th>
                <th className="py-2 pe-3 text-start font-semibold">{t('Configured')}</th>
                <th className="py-2 pe-3 text-start font-semibold">{t('Legal minimum')}</th>
                <th className="py-2 pe-3 text-start font-semibold">{t('Citation')}</th>
                <th className="py-2 text-start font-semibold">{t('Acknowledged')}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((i, n) => (
                <tr key={`${i.source}-${i.code}-${i.companyId}-${n}`} data-testid="below-minimum-row" data-acknowledged={i.acknowledged} className="border-t border-slate-100 align-top dark:border-white/10">
                  <td className="py-2 pe-3">{i.companyName ?? '—'}</td>
                  <td className="py-2 pe-3">
                    <span className="block font-medium">{locale === 'ar' ? i.ruleAr : i.rule}</span>
                    <span className="text-xs text-slate-500">{t(SOURCE_LABEL[i.source])}{i.subject ? ` · ${i.subject}` : ''}</span>
                  </td>
                  <td className="py-2 pe-3 tabular-nums">{i.configured ?? '—'}</td>
                  <td className="py-2 pe-3 tabular-nums">{i.statutory ?? '—'}</td>
                  <td className="py-2 pe-3">{(locale === 'ar' ? i.citationAr : null) || i.citation || '—'}</td>
                  <td className="py-2">
                    {i.acknowledged
                      ? <span>{i.acknowledgedBy ?? '—'}{i.acknowledgedAtUtc ? ` · ${format.date(i.acknowledgedAtUtc.slice(0, 10))}` : ''}{i.note ? <span className="block text-xs text-slate-500">{i.note}</span> : null}</span>
                      : <span className="font-medium text-amber-700 dark:text-amber-300">{t('Not acknowledged')}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
