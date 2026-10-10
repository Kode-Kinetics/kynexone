'use client';

import type { PolicyAskResponse } from '../api/policyDocuments';
import { useT } from '../hooks/useT';

/** Source content is rendered as text, never document-authored HTML or instructions. */
export function PolicyAnswer({ answer }: { answer: PolicyAskResponse }) {
  const t = useT();
  return <div className="space-y-3 rounded-lg border border-sapphire/20 bg-sapphire/5 p-3 dark:border-blue-400/30 dark:bg-blue-400/5">
    <p className="whitespace-pre-wrap break-words text-sm leading-6">{answer.answer}</p>
    {!answer.isGrounded && <p className="text-xs text-amber-800 dark:text-amber-300">{t('No supported policy answer was found. Confirm this with HR.')}</p>}
    {!!answer.citations?.length && <div className="space-y-2">
      <h4 className="text-xs font-semibold">{t('Policy evidence')}</h4>
      {answer.citations.map((citation, index) => <details key={`${citation.documentId}-${citation.chunkIndex}-${index}`} className="rounded border border-slate-200 bg-white p-2 text-xs dark:border-white/10 dark:bg-slate-950">
        <summary className="cursor-pointer font-medium hover:text-sapphire">{citation.source}</summary>
        <blockquote className="mt-2 whitespace-pre-wrap break-words border-s-2 border-sapphire/30 ps-3 leading-5">{citation.excerpt}</blockquote>
        <p className="mt-2 text-slate-500">{t('Document version')}: <span dir="ltr">{citation.versionHash.slice(0, 12)}</span></p>
      </details>)}
    </div>}
    {!answer.citations?.length && !!answer.sources?.length && <p className="text-xs text-slate-600 dark:text-slate-300">{t('Sources')}: {answer.sources.join(', ')}</p>}
    <p className="text-xs text-slate-600 dark:text-slate-400">{t('Policy guidance does not change your records or calculate your live entitlement.')}</p>
  </div>;
}
