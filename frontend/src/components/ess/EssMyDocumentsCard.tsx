'use client';

import { useCallback, useEffect, useState } from 'react';
import { essApi, type EssDocument } from '@/src/api/ess';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import { documentStatusWords, documentVerificationState } from '@/src/lib/documentVerification';
import { StatusChip } from '@/src/components/StatusChip';
import { EssCard, EssEmpty, EssLoadError } from './EssParts';
import { Skeleton } from '@/src/components/ui/Skeleton';

const TONE = { pending: 'amber', verified: 'emerald', approved: 'emerald', rejected: 'rose', expired: 'rose', other: 'slate' } as const;

/**
 * G-004: the documents the employee has given HR and where each stands: waiting, verified, or rejected with the reason HR wrote,
 * so a rejected document is never a dead end ("Rejected" used to be a bare word in a bell notice).
 */
export function EssMyDocumentsCard() {
  const { t } = useLocale();
  const fx = useFormat();
  const [documents, setDocuments] = useState<EssDocument[] | null>(null);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(async () => {
    setError(null);
    try { setDocuments(await essApi.documents()); } catch (e) { setError(e); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  return (
    <EssCard title={t('Documents I gave HR')} testId="ess-my-documents">
      {error ? (
        <EssLoadError error={error} onRetry={() => void load()} />
      ) : documents === null ? (
        <div role="status" aria-busy="true"><Skeleton className="h-16 w-full rounded-xl" /></div>
      ) : documents.length === 0 ? (
        <EssEmpty text={t('You have not given HR any documents yet.')} />
      ) : (
        <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
          {documents.map((d) => {
            const state = documentVerificationState(d.approvalStatus);
            return (
              <li key={d.id} className="py-2.5" data-testid={`ess-doc-${d.id}`}>
                <div className="flex items-center justify-between gap-3">
                  <div className="min-w-0">
                    <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{d.documentType}</p>
                    <p className="text-xs text-slate-400 dark:text-slate-500"><bdi>{d.fileName}</bdi>{d.expiryDate ? <> · {t('Expires on {date}', { date: fx.date(d.expiryDate) })}</> : null}</p>
                  </div>
                  <StatusChip label={t(documentStatusWords(d.approvalStatus))} tone={TONE[state]} dot />
                </div>
                {state === 'rejected' && (
                  <p className="mt-1.5 text-xs text-rose-700 dark:text-rose-300" data-testid={`ess-doc-reason-${d.id}`}>
                    {d.rejectionReason ? t('Reason: {reason}', { reason: d.rejectionReason }) : t('HR did not leave a reason. Ask HR, then upload a new copy.')}
                    {' '}{t('Upload a corrected copy under My employee details.')}
                  </p>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </EssCard>
  );
}
