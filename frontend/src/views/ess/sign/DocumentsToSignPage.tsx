'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { CheckCircle2, FileSignature } from 'lucide-react';
import { esignApi, type SignatureRow } from '../../../api/esign';
import { useLocale } from '../../../contexts/LocaleContext';
import { useEssDate, EssCard, EssEmpty, EssLoadError, EssPageHeader } from '../../../components/ess/EssParts';
import { NotQualifiedBadge } from './SignParts';

/** "Documents to sign": what HR asked this employee to acknowledge, newest first, and what they already have. */
export function DocumentsToSignPage() {
  const { t } = useLocale();
  const date = useEssDate();
  const [rows, setRows] = useState<SignatureRow[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const load = useCallback(async () => { setError(null); try { setRows(await esignApi.list()); } catch (e) { setError(e); } }, []);
  useEffect(() => { void load(); }, [load]);

  const awaiting = (rows ?? []).filter(r => r.status === 'Awaiting');
  const rest = (rows ?? []).filter(r => r.status !== 'Awaiting');
  return (
    <div className="space-y-4">
      <EssPageHeader title={t('Documents to sign')} subtitle={t('Contracts and policies HR asked you to acknowledge. Each one is read in full and confirmed with your password.')} />
      <NotQualifiedBadge />
      {error ? <EssLoadError error={error} onRetry={() => void load()} /> : rows === null ? <EssEmpty text={t('Loading…')} /> : rows.length === 0 ? <EssEmpty text={t('Nothing is waiting for your signature.')} /> : (
        <>
          <EssCard title={t('Waiting for you ({count})', { count: awaiting.length })} testId="sign-awaiting">
            {awaiting.length === 0 ? <EssEmpty text={t('You have signed everything HR asked for.')} /> : (
              <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                {awaiting.map(r => (
                  <li key={r.documentId} className="flex flex-wrap items-center justify-between gap-3 py-3" data-testid="sign-row">
                    <div className="min-w-0"><p className="font-semibold text-slate-800 dark:text-slate-100">{r.title}</p>
                      <p className="text-xs text-slate-500"><bdi dir="ltr">{r.fileName}</bdi> · {t('Requested {date}', { date: date(r.requestedAtUtc) })}</p></div>
                    <Link href={`/ess/sign/${r.documentId}`} className="inline-flex items-center gap-1.5 rounded-xl bg-sapphire px-4 py-2 text-sm font-semibold text-white dark:bg-cyanAccent dark:text-slate-900"><FileSignature className="h-4 w-4" aria-hidden="true" />{t('Read and sign')}</Link>
                  </li>
                ))}
              </ul>
            )}
          </EssCard>
          {rest.length > 0 && (
            <EssCard title={t('Already signed')} testId="sign-done">
              <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                {rest.map(r => (
                  <li key={r.documentId} className="flex items-center justify-between gap-3 py-3 text-sm">
                    <span><span className="font-semibold">{r.title}</span> <span className="text-xs text-slate-500">· {r.status === 'Signed' ? date(r.signedAtUtc) : t('Replaced by a newer version')}</span></span>
                    {r.status === 'Signed' && <span className="inline-flex items-center gap-1 text-emerald-700 dark:text-emerald-300"><CheckCircle2 className="h-4 w-4" aria-hidden="true" />{t('Acknowledged')}</span>}
                  </li>
                ))}
              </ul>
            </EssCard>
          )}
        </>
      )}
    </div>
  );
}
