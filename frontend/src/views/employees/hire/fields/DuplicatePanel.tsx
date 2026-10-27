'use client';

import Link from 'next/link';
import { useState } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';
import { duplicateStatusKey, isNameOnlyMatch, plainYear } from '@/src/lib/hire/duplicate';
import type { DuplicateMatch } from '@/src/api/employees';

export type MatchCard = DuplicateMatch & { joinedYear?: number };
const FORMER = new Set(['Terminated', 'Resigned', 'Exited', 'Archived', 'Offboarded', 'Retired', 'Absconded']);
const MIN_REASON = 10;

/**
 * Non-modal "possible match" panel (A6): the person decides, never silently. "Different person" and "Rehire" each
 * need a reason (audited server-side). Matches the user cannot see arrive masked (canView false) and show no details.
 */
export type PanelCompany = { id: string; legalNameEn: string; legalNameAr?: string | null };

export function DuplicatePanel({ matches, companies = [], differentReason, onDifferent, onRehire, rehireBusy }: {
  matches: MatchCard[]; companies?: PanelCompany[]; differentReason: string | null; onDifferent: (reason: string) => void; onRehire: (match: MatchCard, reason: string) => void; rehireBusy?: boolean;
}) {
  const { t, locale } = useLocale();
  const companyName = (id?: string | null) => { const c = companies.find(x => x.id === id); return c ? (locale === 'ar' && c.legalNameAr ? c.legalNameAr : c.legalNameEn) : ''; };
  const [mode, setMode] = useState<null | { kind: 'different' } | { kind: 'rehire'; match: MatchCard }>(null);
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  if (!matches.length) return null;
  const bad = tried && reason.trim().length < MIN_REASON;
  const confirm = () => {
    setTried(true);
    if (reason.trim().length < MIN_REASON || !mode) return;
    if (mode.kind === 'different') onDifferent(reason.trim()); else onRehire(mode.match, reason.trim());
    setMode(null); setReason(''); setTried(false);
  };
  return (
    <section aria-label={t('Possible match')} className="mb-4 rounded-xl border border-amber-300 bg-amber-50 p-4 text-sm text-amber-950 dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100">
      <h3 className="font-semibold">{t('Possible match')}</h3>
      <ul className="mt-2 space-y-3">
        {matches.map(m => (
          <li key={m.employeeId}>
            {m.canView ? (
              <>
                <p><span className="font-medium">{m.fullName}</span>{' '}
                  <span>{(() => {
                    const details = companyName(m.companyId) ? t('{status}, {company}', { status: t(duplicateStatusKey(m.status)), company: companyName(m.companyId) }) : t(duplicateStatusKey(m.status));
                    return m.joinedYear ? t('{details} · {joined}', { details, joined: t('Joined in {year}', { year: plainYear(m.joinedYear) }) }) : details;
                  })()}</span></p>
                {isNameOnlyMatch(m) ? <p className="mt-0.5 font-medium">{t('Same name — check before continuing.')}</p> : m.matchType === 'strong' ? <p className="mt-0.5">{t('Same ID number')}</p> : null}
              </>
            ) : <p>{t('A person with these details exists in another company you cannot see.')}</p>}
            {m.canView && (
              <div className="mt-2 flex flex-wrap gap-2">
                <Link className="btn-secondary" href={`/people/${m.employeeId}`} target="_blank">{t('Open record')}</Link>
                {FORMER.has(m.status) && <button type="button" className="btn-secondary" onClick={() => { setMode({ kind: 'rehire', match: m }); setReason(''); setTried(false); }}>{t('Rehire this person')}</button>}
              </div>
            )}
          </li>
        ))}
      </ul>
      {differentReason ? <p className="mt-3 font-medium" role="status">{t('Marked as a different person')}</p> : (
        <div className="mt-3">
          {mode?.kind !== 'different' && <button type="button" className="btn-secondary" onClick={() => { setMode({ kind: 'different' }); setReason(''); setTried(false); }}>{t('This is a different person')}</button>}
        </div>
      )}
      {mode && (
        <div className="mt-3">
          <label htmlFor="hire-dup-reason" className="block font-medium">{t('Reason')}</label>
          <textarea id="hire-dup-reason" rows={2} value={reason} onChange={e => setReason(e.target.value)} aria-invalid={bad ? true : undefined} aria-describedby={bad ? 'hire-dup-reason-err' : undefined} className="input mt-1 h-auto w-full" />
          {bad && <p id="hire-dup-reason-err" className="mt-1 text-xs font-medium text-red-700 dark:text-red-300">{t('Enter a reason of at least 10 characters.')}</p>}
          <div className="mt-2 flex gap-2">
            <button type="button" className="btn-primary" disabled={rehireBusy} onClick={confirm}>{mode.kind === 'different' ? t('Confirm different person') : t('Confirm rehire')}</button>
            <button type="button" className="btn-secondary" onClick={() => setMode(null)}>{t('Cancel')}</button>
          </div>
        </div>
      )}
    </section>
  );
}
