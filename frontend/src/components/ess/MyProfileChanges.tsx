'use client';

import { useEffect, useState } from 'react';
import { essApi, type EssProfileChangeRequest } from '../../api/ess';
import { useLocale } from '../../contexts/LocaleContext';
import { legacyFieldLabelKey, profileChangeStatus } from '../../lib/profileChangeStatus';
import { StatusChip } from '../StatusChip';
import { EssCard, useEssDate } from './EssParts';

/**
 * The employee's own older (mobile) personal-detail requests and where each stands: waiting for HR, applied, or sent back
 * with HR's reason. Without this a request could sit with HR and the employee had no way to tell. Silent when there are
 * none, or when the server cannot list them (the rest of the page is unaffected).
 */
export function MyProfileChanges() {
  const { t } = useLocale();
  const fmtDate = useEssDate();
  const [rows, setRows] = useState<EssProfileChangeRequest[]>([]);

  useEffect(() => {
    let live = true;
    essApi.profileChangeRequests().then(r => { if (live) setRows(r); }).catch(() => { if (live) setRows([]); });
    return () => { live = false; };
  }, []);

  if (rows.length === 0) return null;
  return (
    <EssCard title={t('Changes to my details')} testId="my-profile-changes">
      <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
        {rows.map(r => {
          const s = profileChangeStatus(r.status);
          return (
            <li key={r.id} className="flex flex-wrap items-start justify-between gap-2 py-2.5">
              <span className="min-w-0">
                <span className="block text-sm font-semibold text-slate-800 dark:text-slate-100">{r.fields.length ? r.fields.map(f => t(legacyFieldLabelKey(f))).join(', ') : t('Personal details')}</span>
                <span className="block text-xs text-slate-500 dark:text-slate-400">{t('Sent on {date}', { date: fmtDate(r.createdAtUtc) })}</span>
                {r.reviewNote && <span className="mt-1 block text-xs text-slate-600 dark:text-slate-300">{t('HR note')}: <bdi>{r.reviewNote}</bdi></span>}
              </span>
              <StatusChip label={t(s.label)} tone={s.tone} dot />
            </li>
          );
        })}
      </ul>
    </EssCard>
  );
}
