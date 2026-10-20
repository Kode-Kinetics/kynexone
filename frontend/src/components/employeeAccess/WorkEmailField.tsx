'use client';

import { useEffect, useId, useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { assembleWorkEmail, deriveWorkEmailLocalPart, employeesApi, type DeriveWorkEmailResponse } from '../../api/employees';
import { useLocale } from '../../contexts/LocaleContext';
import { workEmailLocalProblem, workEmailProblemKey } from '../../lib/employeeAccess';
import { InfoTip } from '../InfoTip';

/** A complete IT-issued address. Suggestions and conflict alternatives are explicit opt-ins. */
export function WorkEmailField({
  label, value, onChange, englishName, arabicName, companyId, domain, pattern,
  suggest = false, excludeEmployeeId, originalValue, info, infoKey,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  englishName: string;
  arabicName?: string;
  companyId?: string;
  domain: string;
  pattern: string;
  suggest?: boolean;
  excludeEmployeeId?: number;
  originalValue?: string;
  info?: string;
  infoKey?: string;
}) {
  const { t } = useLocale();
  const cleanDomain = (domain ?? '').trim().toLowerCase();
  const inputId = useId();
  const [preview, setPreview] = useState<DeriveWorkEmailResponse | null>(null);
  const [checking, setChecking] = useState(false);
  const [ghost, setGhost] = useState('');

  useEffect(() => {
    setPreview(null);
    setGhost('');
    setChecking(false);
    const wantSuggestion = suggest && !value.trim() && !!cleanDomain && !!englishName.trim();
    if (!wantSuggestion && (!value.trim() || workEmailLocalProblem(value))) return;
    let cancelled = false;
    setChecking(true);
    const handle = setTimeout(async () => {
      let res: DeriveWorkEmailResponse;
      try {
        res = await employeesApi.deriveWorkEmail({
          englishName, arabicName, companyId,
          workEmail: wantSuggestion ? undefined : value.trim(),
          excludeEmployeeId,
        });
      } catch {
        if (!cancelled) {
          setChecking(false);
          if (wantSuggestion) setGhost(assembleWorkEmail(deriveWorkEmailLocalPart(englishName, pattern), cleanDomain));
        }
        return;
      }
      if (cancelled) return;
      setChecking(false);
      if (wantSuggestion) {
        setGhost(res.suggestedWorkEmail || res.workEmail || '');
        setPreview(res.status === 'manual-arabic-only' ? res : null);
      } else {
        setPreview(res);
      }
    }, 300);
    return () => { cancelled = true; clearTimeout(handle); };
  }, [englishName, arabicName, companyId, cleanDomain, pattern, value, suggest, excludeEmployeeId]);

  const showGhost = suggest && !value && !!ghost;
  const changedFromOriginal = originalValue !== undefined && (value ?? '') !== (originalValue ?? '');
  const typedProblem = workEmailProblemKey(workEmailLocalProblem(value));
  const conflict = preview?.workEmail === value.trim() && (preview.status === 'conflict' || preview.unique === false);
  const conflictSuggestion = conflict ? preview?.suggestedWorkEmail : null;

  return (
    <div className="col-span-full block min-w-0 text-sm font-medium text-slate-700 dark:text-slate-300" data-testid="work-email-field">
      <span className="flex min-w-0 items-center gap-1.5 leading-snug">
        <label htmlFor={inputId}>{label} <span className="font-normal text-slate-500">({t('Optional')})</span></label>
        <InfoTip text={info || t('Enter the complete work email address issued by IT, for example name@company.com.')} fieldKey={infoKey} className="ms-1" />
        {changedFromOriginal && <span className="h-1.5 w-1.5 rounded-full bg-sapphire" title="Modified" />}
      </span>
      <input
        id={inputId}
        type="email"
        aria-label={label}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder="name@company.com"
        className="input mt-1.5 w-full"
        spellCheck={false}
        autoCapitalize="none"
        dir="ltr"
      />
      {showGhost && (
        <p className="mt-1 flex flex-wrap items-center gap-2 text-xs text-slate-500">
          <span dir="ltr">{ghost}</span>
          <button type="button" onClick={() => onChange(ghost)} className="flex min-h-11 items-center px-2.5 text-xs font-semibold text-sapphire hover:underline" aria-label={t('Use')}>{t('Use')}</button>
        </p>
      )}
      {conflict ? (
        <p className="mt-1 flex flex-wrap items-center gap-1.5 text-xs text-rose-600 dark:text-rose-400">
          <AlertTriangle className="h-3 w-3 shrink-0" aria-hidden="true" />
          <span>{t('This email is already used by another employee.')}</span>
          {conflictSuggestion && conflictSuggestion !== value.trim() && (
            <button type="button" onClick={() => onChange(conflictSuggestion)} className="font-semibold text-sapphire underline underline-offset-2">{t('Use')} <span dir="ltr">{conflictSuggestion}</span></button>
          )}
        </p>
      ) : checking ? <p className="mt-1 text-xs text-slate-400">{t('Checking…')}</p> : null}
      {typedProblem && <p role="alert" className="mt-1 text-xs font-medium text-rose-600 dark:text-rose-400" data-testid="work-email-problem">{t(typedProblem)}</p>}
      {suggest && <p className="mt-1 text-xs text-slate-500 dark:text-slate-400" data-testid="work-email-help">{t('This is also how they sign in to KynexOne.')}</p>}
      {changedFromOriginal && originalValue && <p className="mt-1 text-xs text-amber-600 dark:text-amber-400">This address is also the self-service (ESS) sign-in identity. Changing it updates the HR record only — it does not rename an existing mailbox or login automatically.</p>}
    </div>
  );
}
