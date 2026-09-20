'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
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
import { RovingTabList, TabPanel } from '../components/ui/RovingTabs';

type Tab = 'requests' | 'issue' | 'register' | 'templates';

const tabs: { id: Tab; label: string; icon: React.ElementType<{ className?: string }> }[] = [
  { id: 'requests', label: 'Employee Requests', icon: Inbox },
  { id: 'issue', label: 'Issue a Letter', icon: Send },
  { id: 'register', label: 'Issued Register', icon: FileSignature },
  { id: 'templates', label: 'Templates', icon: RefreshCw },
];

const LANGUAGES = [
  { value: 'bilingual', label: 'Bilingual (EN + AR)' },
  { value: 'en', label: 'English only' },
  { value: 'ar', label: 'Arabic only' },
];

// ── Shared bits ───────────────────────────────────────────────────────────────

function Spinner({ label }: { label: string }) {
  return (
    <div className="flex items-center justify-center gap-2 py-12 text-sm text-slate-400 dark:text-slate-500">
      <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
      <span>{label}</span>
    </div>
  );
}

function Empty({ title, hint }: { title: string; hint?: string }) {
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
        <ul className="mt-1 list-inside list-disc pl-6 font-mono text-xs">
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

function typeLabel(types: LetterTypeInfo[], key: string) {
  return types.find((t) => t.letterType === key)?.nameEn ?? key;
}

// ── Employee requests queue ───────────────────────────────────────────────────

function RequestsTab({ types, onIssued }: { types: LetterTypeInfo[]; onIssued: () => void }) {
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
      setItems((await hrLettersApi.requests({ status })).items);
    } catch (e) {
      notifyApiError(e);
      setError('The request queue could not be loaded.');
    } finally {
      setLoading(false);
    }
  }, [status]);

  useEffect(() => { void load(); }, [load]);

  const issue = async (request: DocumentRequest) => {
    setBusy(request.id);
    setNote('');
    setRefusal({ message: '' });
    try {
      const { reference } = await hrLettersApi.issueForRequest(request.id);
      setNote(`Issued ${reference} for ${request.employeeName}. The employee can now download it from self-service.`);
      onIssued();
      await load();
    } catch (e) {
      const detail = letterRefusalFrom(e);
      if (detail?.message) setRefusal({ message: detail.message, fields: detail.unresolvedFields });
      else { notifyApiError(e); setRefusal({ message: 'The letter could not be issued.' }); }
    } finally {
      setBusy(null);
    }
  };

  const decline = async () => {
    if (!declining) return;
    setBusy(declining.id);
    try {
      await hrLettersApi.decline(declining.id, reason);
      setNote(`Declined. ${declining.employeeName} has been told why.`);
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
        <select value={status} onChange={(e) => setStatus(e.target.value)} className="select" title="Filter by status">
          {['Pending', 'Issued', 'Declined', 'All'].map((s) => <option key={s}>{s}</option>)}
        </select>
        <span className="text-xs text-slate-400 dark:text-slate-500">{items.length} request{items.length === 1 ? '' : 's'}</span>
      </div>

      <ErrorNote message={error} />
      <ErrorNote message={refusal.message} fields={refusal.fields} />
      <OkNote message={note} />

      <div className="surface overflow-x-auto">
        {loading ? <Spinner label="Loading requests…" /> : items.length === 0 ? (
          <Empty
            title={status === 'Pending' ? 'Nothing waiting on HR.' : `No ${status.toLowerCase()} requests.`}
            hint="Employees raise these from self-service; each one also opens a ticket in the Request Centre."
          />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Employee', 'Document', 'Purpose', 'Addressed to', 'Language', 'Raised', 'Status', ''].map((h) => (
                  <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">{h}</th>
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
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{typeLabel(types, r.letterType)}</td>
                  <td className="max-w-[200px] truncate px-4 py-3 text-slate-600 dark:text-slate-300">{r.purpose || '—'}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.addresseeName || '—'}</td>
                  <td className="px-4 py-3 text-xs uppercase text-slate-500 dark:text-slate-400">{r.language}</td>
                  <td className="px-4 py-3 text-xs text-slate-500 dark:text-slate-400">{new Date(r.createdAtUtc).toLocaleDateString()}</td>
                  <td className="px-4 py-3">
                    <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${
                      r.status === 'Issued' ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400'
                      : r.status === 'Declined' ? 'bg-red-50 text-red-600 dark:bg-red-500/10 dark:text-red-400'
                      : 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400'}`}>
                      {r.status}
                    </span>
                    {r.decisionNote && <p className="mt-1 max-w-[200px] text-xs text-slate-400 dark:text-slate-500">{r.decisionNote}</p>}
                  </td>
                  <td className="px-4 py-3">
                    {r.status === 'Pending' && (
                      <div className="flex gap-1">
                        <button type="button" onClick={() => issue(r)} disabled={busy !== null} className="btn-primary h-7 px-2 text-xs disabled:opacity-60">
                          {busy === r.id ? <Loader2 className="h-3 w-3 animate-spin" /> : <Send className="h-3 w-3" />} Issue
                        </button>
                        <button type="button" onClick={() => { setDeclining(r); setReason(''); }} disabled={busy !== null} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
                          <XCircle className="h-3 w-3" /> Decline
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
        title={`Decline ${declining ? typeLabel(types, declining.letterType) : 'request'}`}
        onClose={() => setDeclining(null)}
        footer={(
          <>
            <button type="button" onClick={() => setDeclining(null)} className="btn-secondary">Cancel</button>
            <button type="button" onClick={decline} disabled={reason.trim().length < 5 || busy !== null} className="btn-primary disabled:opacity-60">Decline</button>
          </>
        )}
      >
        <Field label="Reason (shown to the employee)">
          <textarea
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            rows={3}
            className="input w-full"
            placeholder="e.g. Your bank details are not on file yet — please add them in My Profile first."
          />
        </Field>
        <p className="mt-1 text-xs text-slate-400 dark:text-slate-500">
          At least 5 characters. A decline with no reason is how an employee ends up asking the same question four times.
        </p>
      </Modal>
    </div>
  );
}

// ── Issue directly ────────────────────────────────────────────────────────────

function IssueTab({ types, onIssued }: { types: LetterTypeInfo[]; onIssued: () => void }) {
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

  const configured = useMemo(() => types.filter((t) => t.isConfigured), [types]);

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
      setNote(`Issued ${reference}. It is in the register and the PDF has downloaded.`);
      onIssued();
    } catch (e) {
      const detail = letterRefusalFrom(e);
      if (detail?.message) setRefusal({ message: detail.message, fields: detail.unresolvedFields });
      else { notifyApiError(e); setRefusal({ message: 'The letter could not be issued.' }); }
    } finally {
      setBusy(false);
    }
  };

  if (configured.length === 0) {
    return (
      <div className="surface p-6">
        <Empty
          title="No letter templates are configured for this tenant."
          hint="Open the Templates tab and restore the bilingual defaults, then come back."
        />
      </div>
    );
  }

  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <div className="surface space-y-3 p-4">
        <h3 className="font-semibold text-slate-900 dark:text-white">1. Choose the employee</h3>
        <div className="flex gap-2">
          <input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') void runSearch(); }}
            className="input w-full"
            placeholder="Search by name or employee code"
            aria-label="Search employees"
          />
          <button type="button" onClick={runSearch} disabled={searching} className="btn-secondary h-9 px-3 disabled:opacity-60">
            {searching ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
          </button>
        </div>
        <div className="max-h-72 overflow-y-auto rounded-lg border border-slate-100 dark:border-white/10">
          {searching ? <Spinner label="Searching…" /> : employees.length === 0 ? (
            <Empty title="Search for an employee to begin." />
          ) : employees.map((e) => (
            <button
              key={e.id}
              type="button"
              onClick={() => setEmployeeId(e.id)}
              className={`flex w-full items-center justify-between px-3 py-2 text-left text-sm transition hover:bg-slate-50 dark:hover:bg-white/[0.04] ${
                employeeId === e.id ? 'bg-sapphire/5 dark:bg-sapphire/10' : ''}`}
            >
              <span className="text-slate-900 dark:text-white">{e.fullName}</span>
              <span className="font-mono text-xs text-slate-400 dark:text-slate-500">{e.employeeCode}</span>
            </button>
          ))}
        </div>
      </div>

      <div className="surface space-y-3 p-4">
        <h3 className="font-semibold text-slate-900 dark:text-white">2. Choose the document</h3>
        <Field label="Letter type">
          <select value={letterType} onChange={(e) => setLetterType(e.target.value)} className="select w-full">
            {configured.map((t) => <option key={t.letterType} value={t.letterType}>{t.nameEn} — {t.nameAr}</option>)}
          </select>
        </Field>
        <Field label="Language">
          <select value={language} onChange={(e) => setLanguage(e.target.value)} className="select w-full">
            {LANGUAGES.map((l) => <option key={l.value} value={l.value}>{l.label}</option>)}
          </select>
        </Field>
        <Field label="Purpose">
          <input value={purpose} onChange={(e) => setPurpose(e.target.value)} className="input w-full" placeholder="a bank loan application" />
        </Field>
        <Field label="Addressed to">
          <input value={addressee} onChange={(e) => setAddressee(e.target.value)} className="input w-full" placeholder="Riyad Bank — leave blank for &quot;Whom It May Concern&quot;" />
        </Field>

        <ErrorNote message={refusal.message} fields={refusal.fields} />
        <OkNote message={note} />

        <button type="button" onClick={issue} disabled={busy || employeeId === null} className="btn-primary w-full disabled:opacity-60">
          {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
          {busy ? 'Issuing…' : 'Issue and download'}
        </button>
        <p className="text-xs text-slate-400 dark:text-slate-500">
          Every issuance is recorded in the register with a unique reference, your name as the signatory, and the
          salary figures as they stand today.
        </p>
      </div>
    </div>
  );
}

// ── Register ──────────────────────────────────────────────────────────────────

function RegisterTab({ types, refreshKey }: { types: LetterTypeInfo[]; refreshKey: number }) {
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
      setError('The register could not be loaded.');
    } finally {
      setLoading(false);
    }
  }, [reference, letterType]);

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
          <Field label="Reference number">
            <input
              value={reference}
              onChange={(e) => setReference(e.target.value)}
              className="input w-full"
              placeholder="SAL-CERT-2026-00042"
              aria-label="Reference number"
            />
          </Field>
        </div>
        <div className="min-w-[200px]">
          <Field label="Document">
            <select value={letterType} onChange={(e) => setLetterType(e.target.value)} className="select w-full">
              <option value="">All documents</option>
              {types.map((t) => <option key={t.letterType} value={t.letterType}>{t.nameEn}</option>)}
            </select>
          </Field>
        </div>
        <span className="pb-2 text-xs text-slate-400 dark:text-slate-500">{total} issued</span>
      </div>

      <ErrorNote message={error} />

      <div className="surface overflow-x-auto">
        {loading ? <Spinner label="Loading the register…" /> : items.length === 0 ? (
          <Empty
            title={reference ? `Nothing issued under "${reference}".` : 'No letters have been issued yet.'}
            hint="If a bank quotes a reference that is not here, the document did not come from this system."
          />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Reference', 'Document', 'Employee', 'Purpose', 'Issued by', 'Issued', 'Source', ''].map((h) => (
                  <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {items.map((l) => (
                <tr key={l.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-3 font-mono text-xs font-semibold text-slate-900 dark:text-white">{l.referenceNumber}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{typeLabel(types, l.letterType)}</td>
                  <td className="px-4 py-3">
                    <p className="text-slate-900 dark:text-white">{l.employeeName}</p>
                    <p className="font-mono text-xs text-slate-400 dark:text-slate-500">{l.employeeCode}</p>
                  </td>
                  <td className="max-w-[180px] truncate px-4 py-3 text-slate-600 dark:text-slate-300">{l.purpose || '—'}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{l.issuedByName}</td>
                  <td className="px-4 py-3 text-xs text-slate-500 dark:text-slate-400">{new Date(l.issuedAtUtc).toLocaleString()}</td>
                  <td className="px-4 py-3 text-xs text-slate-500 dark:text-slate-400">{l.fromEmployeeRequest ? 'Employee request' : 'HR'}</td>
                  <td className="px-4 py-3">
                    <button type="button" onClick={() => reprint(l.id)} disabled={busy !== null} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
                      {busy === l.id ? <Loader2 className="h-3 w-3 animate-spin" /> : <Download className="h-3 w-3" />} Reprint
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

function TemplatesTab({ onChanged }: { onChanged: () => void }) {
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
      setError('Templates could not be loaded. This tab needs the Admin or HR Manager role.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const seed = async () => {
    try {
      const { added } = await hrLettersApi.seedDefaults();
      setNote(added === 0 ? 'Every letter type already has a template.' : `Restored ${added} default template(s).`);
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
      setNote(`Saved. New letters of this type will use the updated wording; letters already issued are unchanged.`);
      setEditing(null);
      await load();
      onChanged();
    } catch (e) {
      const detail = letterRefusalFrom(e);
      setError(detail?.message ?? 'The template could not be saved.');
      if (!detail?.message) notifyApiError(e);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <p className="text-xs text-slate-400 dark:text-slate-500">
          Merge fields: {tokens.map((t) => `{{${t}}}`).join(', ')}
        </p>
        <button type="button" onClick={seed} className="btn-secondary h-8 px-3 text-sm">
          <RefreshCw className="h-3.5 w-3.5" /> Restore missing defaults
        </button>
      </div>

      <ErrorNote message={error} />
      <OkNote message={note} />

      <div className="surface overflow-x-auto">
        {loading ? <Spinner label="Loading templates…" /> : items.length === 0 ? (
          <Empty title="No templates yet." hint="Use Restore missing defaults to plant the bilingual starting wording." />
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Document', 'Languages', 'Version', 'Active', 'Updated', ''].map((h) => (
                  <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {items.map((t) => (
                <tr key={t.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-3">
                    <p className="font-medium text-slate-900 dark:text-white">{t.nameEn}</p>
                    <p className="text-xs text-slate-400 dark:text-slate-500">{t.nameAr}</p>
                  </td>
                  <td className="px-4 py-3 text-xs uppercase text-slate-500 dark:text-slate-400">{t.language}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">v{t.version}</td>
                  <td className="px-4 py-3">
                    {t.isActive
                      ? <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-semibold text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400">Active</span>
                      : <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold text-slate-500 dark:bg-white/10 dark:text-slate-400">Inactive</span>}
                  </td>
                  <td className="px-4 py-3 text-xs text-slate-500 dark:text-slate-400">{t.updatedAtUtc ? new Date(t.updatedAtUtc).toLocaleDateString() : '—'}</td>
                  <td className="px-4 py-3">
                    <button type="button" onClick={() => setEditing({ ...t })} className="btn-secondary h-7 px-2 text-xs">Edit wording</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <Modal
        isOpen={editing !== null}
        title={editing ? `Edit: ${editing.nameEn}` : 'Edit template'}
        onClose={() => setEditing(null)}
        size="xl"
        footer={(
          <>
            <button type="button" onClick={() => setEditing(null)} className="btn-secondary">Cancel</button>
            <button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button>
          </>
        )}
      >
        {editing && (
          <div className="space-y-3">
            <Field label="Languages this template can render">
              <select value={editing.language} onChange={(e) => setEditing({ ...editing, language: e.target.value })} className="select w-full">
                {LANGUAGES.map((l) => <option key={l.value} value={l.value}>{l.label}</option>)}
              </select>
            </Field>
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Title (English)">
                <input value={editing.titleEn} onChange={(e) => setEditing({ ...editing, titleEn: e.target.value })} className="input w-full" />
              </Field>
              <Field label="Title (Arabic)">
                <input dir="rtl" value={editing.titleAr} onChange={(e) => setEditing({ ...editing, titleAr: e.target.value })} className="input w-full" />
              </Field>
            </div>
            <Field label="Body (English)">
              <textarea rows={7} value={editing.bodyEn} onChange={(e) => setEditing({ ...editing, bodyEn: e.target.value })} className="input w-full font-mono text-xs" />
            </Field>
            <Field label="Body (Arabic)">
              <textarea dir="rtl" rows={7} value={editing.bodyAr} onChange={(e) => setEditing({ ...editing, bodyAr: e.target.value })} className="input w-full font-mono text-xs" />
            </Field>
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Closing (English)">
                <input value={editing.closingEn} onChange={(e) => setEditing({ ...editing, closingEn: e.target.value })} className="input w-full" />
              </Field>
              <Field label="Closing (Arabic)">
                <input dir="rtl" value={editing.closingAr} onChange={(e) => setEditing({ ...editing, closingAr: e.target.value })} className="input w-full" />
              </Field>
            </div>
            <p className="text-xs text-slate-400 dark:text-slate-500">
              Blank lines separate paragraphs. A merge field that resolves to nothing refuses the issuance rather
              than printing a blank where a salary figure belongs.
            </p>
          </div>
        )}
      </Modal>
    </div>
  );
}

// ── Page ──────────────────────────────────────────────────────────────────────

export default function HrLettersPage() {
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
      setError('The letter catalogue could not be loaded.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void loadTypes(); }, [loadTypes]);

  const unconfigured = types.filter((t) => !t.isConfigured);

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-xl font-bold text-slate-900 dark:text-white">HR Letters</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Salary certificates, bank letters and employment verification — issued from a tenant template, in English
          and Arabic, with a stored reference number.
        </p>
      </div>

      <ErrorNote message={error} />

      {!loading && unconfigured.length > 0 && (
        <p className="rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-500/10 dark:text-amber-400">
          {unconfigured.length} letter type(s) have no template and are hidden from issuance:{' '}
          {unconfigured.map((t) => t.nameEn).join(', ')}. Restore the defaults on the Templates tab.
        </p>
      )}

      <RovingTabList items={tabs} activeId={tab} onChange={setTab} idPrefix="hr-letters" label="HR letters sections" variant="underline" />

      <TabPanel idPrefix="hr-letters" tabId={tab}>
        {loading ? <Spinner label="Loading…" /> : (
          <>
            {tab === 'requests' && <RequestsTab types={types} onIssued={() => setRefreshKey((k) => k + 1)} />}
            {tab === 'issue' && <IssueTab types={types} onIssued={() => setRefreshKey((k) => k + 1)} />}
            {tab === 'register' && <RegisterTab types={types} refreshKey={refreshKey} />}
            {tab === 'templates' && <TemplatesTab onChanged={loadTypes} />}
          </>
        )}
      </TabPanel>
    </div>
  );
}
