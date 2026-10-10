'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { policyDocumentsApi, type PolicyAskResponse } from '../api/policyDocuments';
import { PolicyAnswer } from './PolicyAnswer';
import { useT } from '../hooks/useT';

export function EmployeePolicyAssistant() {
  const t = useT();
  const id = useId();
  const [question, setQuestion] = useState('');
  const [askedQuestion, setAskedQuestion] = useState('');
  const [answer, setAnswer] = useState<PolicyAskResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const request = useRef<AbortController | null>(null);
  useEffect(() => () => request.current?.abort(), []);
  const ask = async () => {
    if (!question.trim() || busy) return;
    const controller = new AbortController(); request.current = controller;
    setBusy(true); setError(''); setAnswer(null); setAskedQuestion(question.trim());
    try { const response = await policyDocumentsApi.employeeAsk(question.trim(), controller.signal); if (!controller.signal.aborted) setAnswer(response); }
    catch { if (!controller.signal.aborted) setError(t('Kody could not check your company policies. Your question is kept; try again.')); }
    finally { if (!controller.signal.aborted) setBusy(false); }
  };
  return <section className="space-y-3" aria-label={t('Company policies')}>
    <p className="text-xs leading-5 text-slate-600 dark:text-slate-300">{t('Ask about approved policies available to your company. Kody shows the supporting passages.')}</p>
    <form noValidate onSubmit={event => { event.preventDefault(); void ask(); }} className="space-y-2">
      <label className="block text-sm font-medium" htmlFor={`${id}-question`}>{t('Ask about your policy')}</label>
      <textarea id={`${id}-question`} value={question} onChange={event => setQuestion(event.target.value)} maxLength={2000} className="input h-24 w-full resize-none" placeholder={t('How does annual leave proration work?')} />
      <div className="flex flex-wrap gap-2">
        <button type="submit" className="btn-primary" disabled={busy || !question.trim()}>{busy ? t('Checking policies…') : t('Ask Kody')}</button>
        {busy && <button type="button" className="btn-secondary" onClick={() => { request.current?.abort(); setBusy(false); }}>{t('Cancel')}</button>}
      </div>
    </form>
    {busy && <p role="status" className="text-xs">{t('Checking policies…')}</p>}
    {error && <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{error}</p>}
    {answer && <div aria-live="polite" className="space-y-2"><p className="text-xs font-medium">{askedQuestion}</p><PolicyAnswer answer={answer} /></div>}
  </section>;
}
