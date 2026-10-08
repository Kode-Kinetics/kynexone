'use client';

import { useRef, useState } from 'react';
import { FileUp } from 'lucide-react';
import { employeeAccessApi, MAX_WORK_EMAIL_ROWS, type EmployeeAccessDto, type WorkEmailBackfillResult } from '../../api/employeeAccess';
import { useLocale } from '../../contexts/LocaleContext';
import { describeApiError } from '../../lib/apiError';
import { BULK_PRINTABLE_STATES, CONFLICT_REASON_KEYS, DEFAULT_CONFLICT_KEY, parseWorkEmailRows, workEmailErrorCode, workEmailLocalProblem, workEmailProblemKey } from '../../lib/employeeAccess';
import { Modal } from '../Modal';
import { fill, Ltr } from './fill';

type Step = 'input' | 'preview' | 'saved';

/** An example row (identifiers, not prose). */
const PASTE_EXAMPLE = 'EMP-0042\tnoah.williams@company.com';

/**
 * "Add work emails": IT's list of employee numbers and work emails, pasted from Excel or uploaded
 * as a CSV. Check (dry run) → "Ready · Not found · Wrong email ending · Already used" → Save →
 * "Give access to these employees now?" → the print flow.
 */
export function AddWorkEmailsDialog({ isOpen, onClose, onSaved, onGiveAccess }: {
  isOpen: boolean;
  onClose: () => void;
  /** The list changed; refresh it. */
  onSaved: () => void;
  /** `print` always prints; `email` lets the server email where it may (the rest still print). */
  onGiveAccess: (employeeIds: number[], names: Record<number, string>, delivery: 'print' | 'email', companyEmails: boolean) => void;

}) {
  const { t } = useLocale();
  const [text, setText] = useState('');
  const [step, setStep] = useState<Step>('input');
  const [preview, setPreview] = useState<WorkEmailBackfillResult | null>(null);
  const [saved, setSaved] = useState<WorkEmailBackfillResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [canEmail, setCanEmail] = useState(false);
  // After saving: who can be given access now, and how many still wait for approval (from each status).
  const [eligibleIds, setEligibleIds] = useState<number[]>([]);
  const [awaitingCount, setAwaitingCount] = useState(0);
  const [error, setError] = useState('');
  const fileRef = useRef<HTMLInputElement>(null);
  const parsed = parseWorkEmailRows(text);
  const tooMany = parsed.rows.length > MAX_WORK_EMAIL_ROWS;
  // Checked as HR pastes, with the server's rule: these rows would be refused, so fix them first.
  const badRows = parsed.rows
    .map((row) => ({ row, key: workEmailProblemKey(workEmailLocalProblem(row.workEmail)) }))
    .filter((x): x is { row: typeof x.row; key: string } => !!x.key);

  const reset = () => { setText(''); setStep('input'); setPreview(null); setSaved(null); setError(''); setBusy(false); setCanEmail(false); setEligibleIds([]); setAwaitingCount(0); };
  const close = () => { reset(); onClose(); };

  const explain = (e: unknown) => {
    // The domain sentence needs the domain, which a 422 does not always name; that one falls back to describeApiError.
    const key = workEmailProblemKey(workEmailErrorCode(e));
    return key ? t(key) : describeApiError(e, t);
  };

  const check = async () => {
    setBusy(true);
    setError('');
    try {
      setPreview(await employeeAccessApi.saveWorkEmails(parsed.rows, true));
      setStep('preview');
    } catch (e) {
      setError(explain(e));
    } finally {
      setBusy(false);
    }
  };

  const save = async () => {
    setBusy(true);
    setError('');
    try {
      const result = await employeeAccessApi.saveWorkEmails(parsed.rows, false);
      setSaved(result);
      await sortOutEligible(result.matched);
      setStep('saved');
      onSaved();
    } catch (e) {
      setError(explain(e));
    } finally {
      setBusy(false);
    }
  };

  /**
   * Who can be given access now. The save response says so per row (accessState + canIssue); an API
   * without those fields falls back to reading each saved employee's status, 20 at a time.
   */
  const sortOutEligible = async (rows: WorkEmailBackfillResult['matched']) => {
    const ids = rows.map((r) => r.employeeId);
    if (rows.length > 0 && rows.every((r) => r.accessState !== undefined && r.canIssue !== undefined)) {
      const eligible = rows.filter((r) => r.canIssue && BULK_PRINTABLE_STATES.has(r.accessState!)).map((r) => r.employeeId);
      setEligibleIds(eligible);
      setAwaitingCount(rows.filter((r) => r.reasonCode === 'awaiting_approval').length);
      // Email delivery is a company fact: one status read answers it.
      setCanEmail(eligible.length > 0 ? await employeeAccessApi.get(eligible[0]).then((a) => !!a.emailDelivery).catch(() => false) : false);
      return;
    }
    try {
      const statuses: EmployeeAccessDto[] = [];
      for (let i = 0; i < ids.length; i += 20) {
        statuses.push(...await Promise.all(ids.slice(i, i + 20).map((id) => employeeAccessApi.get(id))));
      }
      setEligibleIds(statuses.filter((s) => s.canIssue && BULK_PRINTABLE_STATES.has(s.state)).map((s) => s.employeeId));
      setAwaitingCount(statuses.filter((s) => s.reasonCode === 'awaiting_approval').length);
      setCanEmail(statuses.some((s) => !!s.emailDelivery));
    } catch {
      // Statuses unavailable: offer everyone saved; the server still skips (and explains) anyone it must.
      setEligibleIds(ids);
      setAwaitingCount(0);
      setCanEmail(false);
    }
  };

  const onFile = async (file: File | undefined) => {
    if (!file) return;
    setText(await file.text());
    if (fileRef.current) fileRef.current.value = '';
  };

  const ready = preview?.matched.length ?? 0;
  const savedRows = saved?.matched ?? [];
  const savedCount = saved ? (saved.saved || savedRows.length) : 0;

  let footer: React.ReactNode;
  if (step === 'input') {
    footer = (
      <>
        <button type="button" onClick={close} className="btn-secondary">{t('Cancel')}</button>
        <button type="button" onClick={() => void check()} disabled={busy || parsed.rows.length === 0 || tooMany || badRows.length > 0} className="btn-primary disabled:opacity-60">
          {busy ? t('Checking…') : t('Check the list')}
        </button>
      </>
    );
  } else if (step === 'preview') {
    footer = (
      <>
        <button type="button" onClick={() => setStep('input')} className="btn-secondary">{t('Back')}</button>
        <button type="button" onClick={() => void save()} disabled={busy || ready === 0} className="btn-primary disabled:opacity-60">
          {t('Save ({n})', { n: ready })}
        </button>
      </>
    );
  } else {
    footer = (
      <>
        <button type="button" onClick={close} className="btn-secondary">{t('Not now')}</button>
        {eligibleIds.length > 0 && (['email', 'print'] as const).filter((d) => d === 'print' || canEmail).map((delivery) => (
          <button
            key={delivery}
            type="button"
            className={delivery === 'print' ? 'btn-primary' : 'btn-secondary'}
            onClick={() => {
              const ids = [...eligibleIds];
              const names = Object.fromEntries(savedRows.filter((r) => ids.includes(r.employeeId)).map((r) => [r.employeeId, r.employeeName]));
              close();
              onGiveAccess(ids, names, delivery, canEmail);
            }}
          >
            {delivery === 'print'
              ? t('Print sign-in slips ({n})', { n: eligibleIds.length })
              : t('Email sign-in codes ({n})', { n: eligibleIds.length })}
          </button>
        ))}
      </>
    );
  }

  return (
    <Modal isOpen={isOpen} title={t('Add work emails')} size="lg" onClose={close} footer={footer}>
      <div className="space-y-3 text-sm text-slate-700 dark:text-slate-200" data-testid="add-work-emails">
        {error && <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-red-700 dark:bg-red-500/10 dark:text-red-300">{error}</p>}

        {step === 'input' && (
          <>
            <p>{t('Paste two columns from Excel: employee number, then work email. You can also upload a CSV file.')}</p>
            <label className="block">
              <span className="sr-only">{t('Employee numbers and work emails')}</span>
              <textarea
                value={text}
                onChange={(e) => setText(e.target.value)}
                rows={8}
                dir="ltr"
                spellCheck={false}
                className="input w-full font-mono text-xs"
                placeholder={PASTE_EXAMPLE}
                data-testid="work-emails-paste"
              />
            </label>
            <div className="flex flex-wrap items-center justify-between gap-2">
              <label className="btn-secondary cursor-pointer">
                <FileUp className="h-4 w-4" aria-hidden="true" />
                {t('Upload a CSV file')}
                <input ref={fileRef} type="file" accept=".csv,.txt,text/csv,text/plain" className="sr-only" onChange={(e) => void onFile(e.target.files?.[0])} />
              </label>
              <p className="text-xs text-slate-500" data-testid="work-emails-row-count">
                {t('{count, plural, one {# row found.} other {# rows found.}}', { count: parsed.rows.length })}
                {parsed.unreadable > 0 && <> {t('{count, plural, one {# line could not be read.} other {# lines could not be read.}}', { count: parsed.unreadable })}</>}
              </p>
            </div>
            {tooMany && <p role="alert" className="text-xs text-rose-700">{t('Send at most {max} rows at a time.', { max: MAX_WORK_EMAIL_ROWS })}</p>}
            {badRows.length > 0 && (
              <ul role="alert" className="max-h-32 space-y-0.5 overflow-y-auto rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-xs text-rose-800" data-testid="work-emails-invalid">
                {badRows.slice(0, 50).map(({ row, key }) => (
                  <li key={`${row.employeeCode}-${row.workEmail}`}>
                    <Ltr className="font-semibold">{row.employeeCode}</Ltr>
                    <span aria-hidden="true"> · </span>
                    <Ltr>{row.workEmail}</Ltr>
                    <span aria-hidden="true"> · </span>
                    {t(key)}
                  </li>
                ))}
              </ul>
            )}
          </>
        )}

        {step === 'preview' && preview && (
          <>
            <p className="rounded-lg bg-slate-50 px-3 py-2 font-semibold text-slate-900 dark:bg-white/[0.05] dark:text-white" data-testid="work-emails-preview">
              {t('Ready: {a} · Not found: {b} · Wrong email ending: {c} · Already used: {d}', {
                a: preview.matched.length, b: preview.notFound.length, c: preview.wrongDomain.length, d: preview.conflicts.length,
              })}
            </p>
            {(preview.notFound.length > 0 || preview.wrongDomain.length > 0 || preview.conflicts.length > 0) && (
              <ul className="max-h-56 space-y-1 overflow-y-auto rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
                {preview.notFound.map((code) => (
                  <li key={`nf-${code}`}>{fill(t('No employee has the number {code}.'), { code: <Ltr>{code}</Ltr> })}</li>
                ))}
                {preview.wrongDomain.map((row) => (
                  <li key={`wd-${row.employeeCode}-${row.workEmail}`}>
                    <Ltr className="font-semibold">{row.employeeCode}</Ltr>
                    <span aria-hidden="true"> · </span>
                    {fill(t('{email} does not end in @{domain}.'), { email: <Ltr>{row.workEmail}</Ltr>, domain: <Ltr>{row.expectedDomain}</Ltr> })}
                  </li>
                ))}
                {preview.conflicts.map((row) => {
                  const key = CONFLICT_REASON_KEYS[row.reason];
                  const sentence = key === CONFLICT_REASON_KEYS.username_differs && !row.username ? null : key;
                  return (
                    <li key={`cf-${row.employeeCode}-${row.workEmail}`}>
                      <Ltr className="font-semibold">{row.employeeCode}</Ltr>
                      <span aria-hidden="true"> · </span>
                      {sentence
                        ? fill(t(sentence), { username: <Ltr>{row.username}</Ltr> })
                        : t(DEFAULT_CONFLICT_KEY)}
                    </li>
                  );
                })}
              </ul>
            )}
          </>
        )}

        {step === 'saved' && saved && (
          <div className="space-y-2" data-testid="work-emails-saved">
            <p className="font-semibold text-slate-900 dark:text-white">{t('{count, plural, one {# work email saved.} other {# work emails saved.}}', { count: savedCount })}</p>
            {eligibleIds.length > 0 && <p>{t('Give access to these employees now ({n})?', { n: eligibleIds.length })}</p>}
            {awaitingCount > 0 && <p className="text-xs text-slate-600 dark:text-slate-300" data-testid="work-emails-awaiting">{t('Waiting for approval, so not included: {n}.', { n: awaitingCount })}</p>}
          </div>
        )}
      </div>
    </Modal>
  );
}
