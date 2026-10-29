'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import {
  AlertTriangle, CheckCircle2, Download, FileSignature, Inbox, Loader2, RefreshCw, Search, Send, XCircle,
} from 'lucide-react';
import { notifyApiError } from '../api/client';
import { employeesApi, type EmployeeListItem } from '../api/employees';
import {
  hrLettersApi, letterRefusalFrom,
  type DocumentRequest, type IssuedLetter, type LetterTemplate, type LetterTypeInfo,
} from '../api/hrLetters';
import { Modal } from '../components/Modal';
import { ModuleSettingsLink } from '../components/setup/ModuleSettingsLink';
import { useAuth } from '../contexts/AuthContext';
import { useT } from '../hooks/useT';
import { useFormat } from '../hooks/useFormat';
import { useLocale } from '../contexts/LocaleContext';
import { EnumLabel, enumLabel } from '../components/EnumLabel';
import { msg } from '../i18n/translations';
import { RovingTabList, TabPanel } from '../components/ui/RovingTabs';

/** An example reference number: a format, not a sentence. */
const REFERENCE_EXAMPLE = 'SAL-CERT-2026-00042';

type Tab = 'requests' | 'issue' | 'register';

const tabs: { id: Tab; label: string; icon: React.ElementType<{ className?: string }> }[] = [
  { id: 'requests', label: msg('Employee Requests'), icon: Inbox },
  { id: 'issue', label: msg('Issue a Letter'), icon: Send },
  { id: 'register', label: msg('Issued Register'), icon: FileSignature },
];

const LANGUAGES = [
  { value: 'bilingual', label: msg('Bilingual (EN + AR)') },
  { value: 'en', label: msg('English only') },
  { value: 'ar', label: msg('Arabic only') },
];

/** The language of a letter (`bilingual`, `en`, `ar`) in the viewer's language. */
function languageLabel(t: (key: string) => string, value: string) {
  const known = LANGUAGES.find((l) => l.value === value);
  return known ? t(known.label) : value;
}

// ── Shared bits ───────────────────────────────────────────────────────────────

function Spinner({ label }: { label: string }) {
  return (
    <div className="flex items-center justify-center gap-2 py-12 text-sm text-slate-400 dark:text-slate-500">
      <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
      <span>{label}</span>
    </div>
  );
}

function Empty({ title, hint }: { title: string; hint?: React.ReactNode }) {
  return (
    <div className="py-12 text-center">
      <p className="text-sm font-medium text-slate-500 dark:text-slate-400">{title}</p>
      {hint && <p className="mt-1 text-xs text-slate-400 dark:text-slate-500">{hint}</p>}
    </div>
  );
}

function ErrorNote({ message, fields }: { message: string; fields?: string[] }) {
  if (!message) return null;
  return (
    <div className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-500/10 dark:text-red-400">
      <p className="flex items-start gap-2">
        <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
        <span>{message}</span>
      </p>
      {fields && fields.length > 0 && (
        <ul className="mt-1 list-inside list-disc ps-6 font-mono text-xs">
          {fields.map((f) => <li key={f}>{f}</li>)}
        </ul>
      )}
    </div>
  );
}

function OkNote({ message }: { message: string }) {
  if (!message) return null;
  return (
    <p className="flex items-center gap-2 rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400">
      <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden="true" />
      {message}
    </p>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">{label}</span>
      {children}
    </label>
  );
}

function typeLabel(types: LetterTypeInfo[], key: string, locale?: string) {
  const info = types.find((ty) => ty.letterType === key);
  if (!info) return key;
  return locale === 'ar' && info.nameAr ? info.nameAr : info.nameEn;
}

// ── Employee requests queue ───────────────────────────────────────────────────

function RequestsTab({ types, onIssued }: { types: LetterTypeInfo[]; onIssued: () => void }) {
  const t = useT();
  const fx = useFormat();
  const { locale } = useLocale();
  const [items, setItems] = useState<DocumentRequest[]>([]);
  const [status, setStatus] = useState('Pending');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [note, setNote] = useState('');
  const [refusal, setRefusal] = useState<{ message: string; fields?: string[] }>({ message: '' });
  const [declining, setDeclining] = useState<DocumentRequest | null>(null);
  const [reason, setReason] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      setItems(await hrLettersApi.allRequests({ status }));
    } catch (e) {
      notifyApiError(e);
      setError(t('The request queue could not be loaded.'));
    } finally {
      setLoading(false);
    }
  }, [status, t]);

  useEffect(() => { void load(); }, [load]);

  const issue = async (request: DocumentRequest) => {
    setBusy(request.id);
    setNote('');
    setRefusal({ message: '' });
    try {
      const { reference } = await hrLettersApi.issueForRequest(request.id);
      setNote(t('Issued {reference} for {employee}. The employee can now download it from self-service.', { reference, employee: request.employeeName }));
      onIssued();
      await load();
    } catch (e) {
      const detail = letterRefusalFrom(e);
      if (detail?.message) setRefusal({ message: detail.message, fields: detail.unresolvedFields });
      else { notifyApiError(e); setRefusal({ message: t('The letter could not be issued.') }); }
    } finally {
      setBusy(null);
    }
  };

  const decline = async () => {
    if (!declining) return;
    setBusy(declining.id);
    try {
      await hrLettersApi.decline(declining.id, reason);
      setNote(t('Declined. {employee} has been told why.', { employee: declining.employeeName }));
      setDeclining(null);
      setReason('');
      await load();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2">
        <select value={status} onChange={(e) => setStatus(e.target.value)} className="select" title={t('Filter by status')} aria-label={t('Filter by status')}>
          {['Pending', 'Issued', 'Declined', 'All'].map((s) => <option key={s} value={s}>{s === 'All' ? t('All') : enumLabel(t, 'HrRequestStatus', s)}</option>)}
        </select>
        <span className="text-xs text-slate-400 dark:text-slate-500">{t('{count, plural, one {# request} other {# requests}}', { count: items.length })}</span>
      </div>

      <ErrorNote message={error} />
      <ErrorNote message={refusal.message} fields={refusal.fields} />
      <OkNote message={note} />

      <div className="surface overflow-x-auto">
        {loading ? <Spinner label={t('Loading requests…')} /> : items.length === 0 ? (
          <Empty
            title={status === 'Pending' ? t('Nothing waiting on HR.') : status === 'Issued' ? t('No issued requests.') : status === 'Declined' ? t('No declined requests.') : t('No requests.')}
            hint={t('Employees raise these from self-service; each one also opens a ticket in the Request Centre.')}
          />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[msg('Employee'), msg('Document'), msg('Purpose'), msg('Addressed to'), msg('Language'), msg('Raised'), msg('Status'), ''].map((h, i) => (
                  <th key={h || `col-${i}`} className="px-4 py-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">{h ? t(h) : <span className="sr-only">{t('Actions')}</span>}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {items.map((r) => (
                <tr key={r.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-3">
                    <p className="font-medium text-slate-900 dark:text-white">{r.employeeName}</p>
                    <p className="font-mono text-xs text-slate-400 dark:text-slate-500">{r.employeeCode}</p>
                  </td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{typeLabel(types, r.letterType, locale)}</td>
                  <td className="max-w-[200px] truncate px-4 py-3 text-slate-600 dark:text-slate-300">{r.purpose || '—'}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.addresseeName || '—'}</td>
                  <td className="px-4 py-3 text-xs uppercase text-slate-500 dark:text-slate-400">{languageLabel(t, r.language)}</td>
                  <td className="px-4 py-3 text-xs text-slate-500 dark:text-slate-400">{fx.date(r.createdAtUtc)}</td>
                  <td className="px-4 py-3">
                    <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${
                      r.status === 'Issued' ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400'
                      : r.status === 'Declined' ? 'bg-red-50 text-red-600 dark:bg-red-500/10 dark:text-red-400'
                      : 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400'}`}>
                      <EnumLabel enum="HrRequestStatus" value={r.status} />
                    </span>
                    {r.decisionNote && <p className="mt-1 max-w-[200px] text-xs text-slate-400 dark:text-slate-500">{r.decisionNote}</p>}
                  </td>
                  <td className="px-4 py-3">
                    {r.status === 'Pending' && (
                      <div className="flex gap-1">
                        <button type="button" onClick={() => issue(r)} disabled={busy !== null} className="btn-primary h-7 px-2 text-xs disabled:opacity-60">
                          {busy === r.id ? <Loader2 className="h-3 w-3 animate-spin" /> : <Send className="h-3 w-3" />} {t('Issue')}
                        </button>
                        <button type="button" onClick={() => { setDeclining(r); setReason(''); }} disabled={busy !== null} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
                          <XCircle className="h-3 w-3" /> {t('Decline')}
                        </button>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <Modal
        isOpen={declining !== null}
        title={declining ? t('Decline {document}', { document: typeLabel(types, declining.letterType, locale) }) : t('Decline request')}
        onClose={() => setDeclining(null)}
        footer={(
          <>
            <button type="button" onClick={() => setDeclining(null)} className="btn-secondary">{t('Cancel')}</button>
            <button type="button" onClick={decline} disabled={reason.trim().length < 5 || busy !== null} className="btn-primary disabled:opacity-60">{t('Decline')}</button>
          </>
        )}
      >
        <Field label={t('Reason (shown to the employee)')}>
          <textarea
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            rows={3}
            className="input w-full"
            placeholder={t('Example: Your bank details are not on file yet — please add them in My Profile first.')}
          />
        </Field>
        <p className="mt-1 text-xs text-slate-400 dark:text-slate-500">
          {t('At least 5 characters. A decline with no reason is how an employee ends up asking the same question four times.')}
        </p>
      </Modal>
    </div>
  );
}

// ── Issue directly ────────────────────────────────────────────────────────────

function IssueTab({ types, onIssued }: { types: LetterTypeInfo[]; onIssued: () => void }) {
  const t = useT();
  const [search, setSearch] = useState('');
  const [employees, setEmployees] = useState<EmployeeListItem[]>([]);
  const [searching, setSearching] = useState(false);
  const [employeeId, setEmployeeId] = useState<number | null>(null);
  const [letterType, setLetterType] = useState('');
  const [language, setLanguage] = useState('bilingual');
  const [purpose, setPurpose] = useState('');
  const [addressee, setAddressee] = useState('');
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState('');
  const [refusal, setRefusal] = useState<{ message: string; fields?: string[] }>({ message: '' });

  const configured = useMemo(() => types.filter((ty) => ty.isConfigured), [types]);

  useEffect(() => {
    if (!letterType && configured.length > 0) setLetterType(configured[0].letterType);
  }, [configured, letterType]);

  const runSearch = async () => {
    setSearching(true);
    try {
      setEmployees((await employeesApi.list({ search, status: 'Active', pageSize: 20 })).items);
    } catch (e) {
      notifyApiError(e);
    } finally {
      setSearching(false);
    }
  };

  const issue = async () => {
    if (employeeId === null || !letterType) return;
    setBusy(true);
    setNote('');
    setRefusal({ message: '' });
    try {
      const { reference } = await hrLettersApi.issue({ employeeId, letterType, language, purpose, addresseeName: addressee });
      setNote(t('Issued {reference}. It is in the register and the PDF has downloaded.', { reference }));
      onIssued();
    } catch (e) {
      const detail = letterRefusalFrom(e);
      if (detail?.message) setRefusal({ message: detail.message, fields: detail.unresolvedFields });
      else { notifyApiError(e); setRefusal({ message: t('The letter could not be issued.') }); }
    } finally {
      setBusy(false);
    }
  };

  if (configured.length === 0) {
    return (
      <div className="surface p-6">
        <Empty
          title={t('No letter templates are configured for this tenant.')}
          hint={<RestoreDefaultsHint />}
        />
      </div>
    );
  }

  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <div className="surface space-y-3 p-4">
        <h3 className="font-semibold text-slate-900 dark:text-white">{t('1. Choose the employee')}</h3>
        <div className="flex gap-2">
          <input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') void runSearch(); }}
            className="input w-full"
            placeholder={t('Search by name or employee code')}
            aria-label={t('Search employees')}
          />
          <button type="button" onClick={runSearch} disabled={searching} aria-label={t('Search')} title={t('Search')} className="btn-secondary h-9 px-3 disabled:opacity-60">
            {searching ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
          </button>
        </div>
        <div className="max-h-72 overflow-y-auto rounded-lg border border-slate-100 dark:border-white/10">
          {searching ? <Spinner label={t('Searching…')} /> : employees.length === 0 ? (
            <Empty title={t('Search for an employee to begin.')} />
          ) : employees.map((e) => (
            <button
              key={e.id}
              type="button"
              onClick={() => setEmployeeId(e.id)}
              className={`flex w-full items-center justify-between px-3 py-2 text-start text-sm transition hover:bg-slate-50 dark:hover:bg-white/[0.04] ${
                employeeId === e.id ? 'bg-sapphire/5 dark:bg-sapphire/10' : ''}`}
            >
              <span className="text-slate-900 dark:text-white">{e.fullName}</span>
              <span className="font-mono text-xs text-slate-400 dark:text-slate-500">{e.employeeCode}</span>
            </button>
          ))}
        </div>
      </div>

      <div className="surface space-y-3 p-4">
        <h3 className="font-semibold text-slate-900 dark:text-white">{t('2. Choose the document')}</h3>
        <Field label={t('Letter type')}>
          <select value={letterType} onChange={(e) => setLetterType(e.target.value)} className="select w-full">
            {configured.map((ty) => <option key={ty.letterType} value={ty.letterType}>{`${ty.nameEn} — ${ty.nameAr}`}</option>)}
          </select>
        </Field>
        <Field label={t('Language')}>
          <select value={language} onChange={(e) => setLanguage(e.target.value)} className="select w-full">
            {LANGUAGES.map((l) => <option key={l.value} value={l.value}>{t(l.label)}</option>)}
          </select>
        </Field>
        <Field label={t('Purpose')}>
          <input value={purpose} onChange={(e) => setPurpose(e.target.value)} className="input w-full" placeholder={t('Example: a bank loan application')} />
        </Field>
        <Field label={t('Addressed to')}>
          <input value={addressee} onChange={(e) => setAddressee(e.target.value)} className="input w-full" placeholder={t('Example: Riyad Bank — leave blank for "Whom It May Concern"')} />
        </Field>

        <ErrorNote message={refusal.message} fields={refusal.fields} />
        <OkNote message={note} />

        <button type="button" onClick={issue} disabled={busy || employeeId === null} className="btn-primary w-full disabled:opacity-60">
          {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
          {busy ? t('Issuing…') : t('Issue and download')}
        </button>
        <p className="text-xs text-slate-400 dark:text-slate-500">
          {t('Every issuance is recorded in the register with a unique reference, your name as the signatory, and the salary figures as they stand today.')}
        </p>
      </div>
    </div>
  );
}

// ── Register ──────────────────────────────────────────────────────────────────

function RegisterTab({ types, refreshKey }: { types: LetterTypeInfo[]; refreshKey: number }) {
  const t = useT();
  const fx = useFormat();
  const { locale } = useLocale();
  const [items, setItems] = useState<IssuedLetter[]>([]);
  const [total, setTotal] = useState(0);
  const [reference, setReference] = useState('');
  const [letterType, setLetterType] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const page = await hrLettersApi.register({
        reference: reference.trim() || undefined,
        letterType: letterType || undefined,
        pageSize: 50,
      });
      setItems(page.items);
      setTotal(page.total);
    } catch (e) {
      notifyApiError(e);
      setError(t('The register could not be loaded.'));
    } finally {
      setLoading(false);
    }
  }, [reference, letterType, t]);

  useEffect(() => { void load(); }, [load, refreshKey]);

  const reprint = async (id: string) => {
    setBusy(id);
    try { await hrLettersApi.reprint(id); }
    catch (e) { notifyApiError(e); }
    finally { setBusy(null); }
  };

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-end gap-2">
        <div className="min-w-[220px] flex-1">
          <Field label={t('Reference number')}>
            <input
              value={reference}
              onChange={(e) => setReference(e.target.value)}
              className="input w-full"
              placeholder={REFERENCE_EXAMPLE}
              aria-label={t('Reference number')}
            />
          </Field>
        </div>
        <div className="min-w-[200px]">
          <Field label={t('Document')}>
            <select value={letterType} onChange={(e) => setLetterType(e.target.value)} className="select w-full">
              <option value="">{t('All documents')}</option>
              {types.map((ty) => <option key={ty.letterType} value={ty.letterType}>{locale === 'ar' && ty.nameAr ? ty.nameAr : ty.nameEn}</option>)}
            </select>
          </Field>
        </div>
        <span className="pb-2 text-xs text-slate-400 dark:text-slate-500">{t('{count} issued', { count: total })}</span>
      </div>

      <ErrorNote message={error} />

      <div className="surface overflow-x-auto">
        {loading ? <Spinner label={t('Loading the register…')} /> : items.length === 0 ? (
          <Empty
            title={reference ? t('Nothing issued under "{reference}".', { reference }) : t('No letters have been issued yet.')}
            hint={t('If a bank quotes a reference that is not here, the document did not come from this system.')}
          />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[msg('Reference'), msg('Document'), msg('Employee'), msg('Purpose'), msg('Issued by'), msg('Issued'), msg('Source'), ''].map((h, i) => (
                  <th key={h || `col-${i}`} className="px-4 py-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">{h ? t(h) : <span className="sr-only">{t('Actions')}</span>}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {items.map((l) => (
                <tr key={l.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-3 font-mono text-xs font-semibold text-slate-900 dark:text-white">{l.referenceNumber}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{typeLabel(types, l.letterType, locale)}</td>
                  <td className="px-4 py-3">
                    <p className="text-slate-900 dark:text-white">{l.employeeName}</p>
                    <p className="font-mono text-xs text-slate-400 dark:text-slate-500">{l.employeeCode}</p>
                  </td>
                  <td className="max-w-[180px] truncate px-4 py-3 text-slate-600 dark:text-slate-300">{l.purpose || '—'}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{l.issuedByName}</td>
                  <td className="px-4 py-3 text-xs text-slate-500 dark:text-slate-400">{fx.dateTime(l.issuedAtUtc)}</td>
                  <td className="px-4 py-3 text-xs text-slate-500 dark:text-slate-400">{l.fromEmployeeRequest ? t('Employee request') : t('HR')}</td>
                  <td className="px-4 py-3">
                    <button type="button" onClick={() => reprint(l.id)} disabled={busy !== null} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
                      {busy === l.id ? <Loader2 className="h-3 w-3 animate-spin" /> : <Download className="h-3 w-3" />} {t('Reprint')}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

// ── Templates ─────────────────────────────────────────────────────────────────

/** The letter templates editor: the Setup section Letter templates shows it. */
export function TemplatesTab({ onChanged }: { onChanged: () => void }) {
  const t = useT();
  const fx = useFormat();
  const [items, setItems] = useState<LetterTemplate[]>([]);
  const [tokens, setTokens] = useState<string[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [note, setNote] = useState('');
  const [editing, setEditing] = useState<LetterTemplate | null>(null);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const data = await hrLettersApi.templates();
      setItems(data.items);
      setTokens(data.knownTokens);
    } catch (e) {
      notifyApiError(e);
      setError(t('Templates could not be loaded. This needs the Admin or HR Manager role.'));
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { void load(); }, [load]);

  const seed = async () => {
    try {
      const { added } = await hrLettersApi.seedDefaults();
      setNote(added === 0 ? t('Every letter type already has a template.') : t('Restored {count} default template(s).', { count: added }));
      await load();
      onChanged();
    } catch (e) { notifyApiError(e); }
  };

  const save = async () => {
    if (!editing) return;
    setSaving(true);
    setError('');
    try {
      await hrLettersApi.updateTemplate(editing.id, editing);
      setNote(t('Saved. New letters of this type will use the updated wording; letters already issued are unchanged.'));
      setEditing(null);
      await load();
      onChanged();
    } catch (e) {
      const detail = letterRefusalFrom(e);
      setError(detail?.message ?? t('The template could not be saved.'));
      if (!detail?.message) notifyApiError(e);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <p className="text-xs text-slate-400 dark:text-slate-500">
          {t('Merge fields: {fields}', { fields: tokens.map((tok) => `{{${tok}}}`).join(fx.locale === 'ar' ? '، ' : ', ') })}
        </p>
        <button type="button" onClick={seed} className="btn-secondary h-8 px-3 text-sm">
          <RefreshCw className="h-3.5 w-3.5" /> {t('Restore missing defaults')}
        </button>
      </div>

      <ErrorNote message={error} />
      <OkNote message={note} />

      <div className="surface overflow-x-auto">
        {loading ? <Spinner label={t('Loading templates…')} /> : items.length === 0 ? (
          <Empty title={t('No templates yet.')} hint={t('Use Restore missing defaults to plant the bilingual starting wording.')} />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[msg('Document'), msg('Languages'), msg('Version'), msg('Active'), msg('Updated'), ''].map((h, i) => (
                  <th key={h || `col-${i}`} className="px-4 py-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">{h ? t(h) : <span className="sr-only">{t('Actions')}</span>}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {items.map((tpl) => (
                <tr key={tpl.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-3">
                    <p className="font-medium text-slate-900 dark:text-white">{tpl.nameEn}</p>
                    <p className="text-xs text-slate-400 dark:text-slate-500">{tpl.nameAr}</p>
                  </td>
                  <td className="px-4 py-3 text-xs uppercase text-slate-500 dark:text-slate-400">{languageLabel(t, tpl.language)}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{t('Version {version}', { version: tpl.version })}</td>
                  <td className="px-4 py-3">
                    {tpl.isActive
                      ? <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-semibold text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400">{t('Active')}</span>
                      : <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold text-slate-500 dark:bg-white/10 dark:text-slate-400">{t('Inactive')}</span>}
                  </td>
                  <td className="px-4 py-3 text-xs text-slate-500 dark:text-slate-400">{tpl.updatedAtUtc ? fx.date(tpl.updatedAtUtc) : '—'}</td>
                  <td className="px-4 py-3">
                    <button type="button" onClick={() => setEditing({ ...tpl })} className="btn-secondary h-7 px-2 text-xs">{t('Edit wording')}</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <Modal
        isOpen={editing !== null}
        title={editing ? t('Edit: {name}', { name: editing.nameEn }) : t('Edit template')}
        onClose={() => setEditing(null)}
        size="xl"
        footer={(
          <>
            <button type="button" onClick={() => setEditing(null)} className="btn-secondary">{t('Cancel')}</button>
            <button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? t('Saving…') : t('Save')}</button>
          </>
        )}
      >
        {editing && (
          <div className="space-y-3">
            <Field label={t('Languages this template can render')}>
              <select value={editing.language} onChange={(e) => setEditing({ ...editing, language: e.target.value })} className="select w-full">
                {LANGUAGES.map((l) => <option key={l.value} value={l.value}>{t(l.label)}</option>)}
              </select>
            </Field>
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label={t('Title (English)')}>
                <input value={editing.titleEn} onChange={(e) => setEditing({ ...editing, titleEn: e.target.value })} className="input w-full" />
              </Field>
              <Field label={t('Title (Arabic)')}>
                <input dir="rtl" value={editing.titleAr} onChange={(e) => setEditing({ ...editing, titleAr: e.target.value })} className="input w-full" />
              </Field>
            </div>
            <Field label={t('Body (English)')}>
              <textarea rows={7} value={editing.bodyEn} onChange={(e) => setEditing({ ...editing, bodyEn: e.target.value })} className="input w-full font-mono text-xs" />
            </Field>
            <Field label={t('Body (Arabic)')}>
              <textarea dir="rtl" rows={7} value={editing.bodyAr} onChange={(e) => setEditing({ ...editing, bodyAr: e.target.value })} className="input w-full font-mono text-xs" />
            </Field>
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label={t('Closing (English)')}>
                <input value={editing.closingEn} onChange={(e) => setEditing({ ...editing, closingEn: e.target.value })} className="input w-full" />
              </Field>
              <Field label={t('Closing (Arabic)')}>
                <input dir="rtl" value={editing.closingAr} onChange={(e) => setEditing({ ...editing, closingAr: e.target.value })} className="input w-full" />
              </Field>
            </div>
            <p className="text-xs text-slate-400 dark:text-slate-500">
              {t('Blank lines separate paragraphs. A merge field that resolves to nothing refuses the issuance rather than printing a blank where a salary figure belongs.')}
            </p>
          </div>
        )}
      </Modal>
    </div>
  );
}

// ── Page ──────────────────────────────────────────────────────────────────────

/** Where to restore the default templates: Setup → Letter templates for Admin and HR Manager, who are the ones the API lets. */
function RestoreDefaultsHint() {
  const t = useT();
  const { hasRole } = useAuth();
  if (!hasRole('Admin') && !hasRole('HR Manager')) return <>{t('Ask an Admin or HR Manager to restore the bilingual defaults in Setup.')}</>;
  return <Link href="/setup?tab=letterTemplates" className="font-semibold underline">{t('Restore the bilingual defaults in Setup → Letter templates.')}</Link>;
}

export default function HrLettersPage() {
  const t = useT();
  const [tab, setTab] = useState<Tab>('requests');
  const [types, setTypes] = useState<LetterTypeInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [refreshKey, setRefreshKey] = useState(0);

  const loadTypes = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      setTypes(await hrLettersApi.types());
    } catch (e) {
      notifyApiError(e);
      setError(t('The letter catalogue could not be loaded.'));
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { void loadTypes(); }, [loadTypes]);

  const unconfigured = types.filter((ty) => !ty.isConfigured);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold text-slate-900 dark:text-white">{t('HR Letters')}</h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            {t('Salary certificates, bank letters and employment verification — issued from a tenant template, in English and Arabic, with a stored reference number.')}
          </p>
        </div>
        <ModuleSettingsLink sections={['letterTemplates']} label={msg('Letter settings')} />
      </div>

      <ErrorNote message={error} />

      {!loading && unconfigured.length > 0 && (
        <p className="rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-500/10 dark:text-amber-400">
          {t('{count} letter type(s) have no template and are hidden from issuance: {names}.', { count: unconfigured.length, names: unconfigured.map((ty) => ty.nameEn).join(', ') })}{' '}<RestoreDefaultsHint />
        </p>
      )}

      <RovingTabList items={tabs.map((x) => ({ ...x, label: t(x.label) }))} activeId={tab} onChange={setTab} idPrefix="hr-letters" label={t('HR letters sections')} variant="underline" />

      <TabPanel idPrefix="hr-letters" tabId={tab}>
        {loading ? <Spinner label={t('Loading…')} /> : (
          <>
            {tab === 'requests' && <RequestsTab types={types} onIssued={() => setRefreshKey((k) => k + 1)} />}
            {tab === 'issue' && <IssueTab types={types} onIssued={() => setRefreshKey((k) => k + 1)} />}
            {tab === 'register' && <RegisterTab types={types} refreshKey={refreshKey} />}
          </>
        )}
      </TabPanel>
    </div>
  );
}
