'use client';

import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { useRouter } from 'next/navigation';
import { Download, KeyRound, Plus, ShieldCheck } from 'lucide-react';
import {
  API_KEY_MAX_DAYS, API_KEY_SCOPE_CONFIRM, API_KEY_SCOPE_READ, apiKeyRefusal, glApiKeysApi,
  type ApiKeyScope, type ApiKeySummary, type CreateApiKeyResponse,
} from '../../api/glApiKeys';
import { saveJournalFile } from '../../api/financeErp';
import type { CompanyDto } from '../../api/organization';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { Modal } from '../Modal';
import {
  Badge, Field, FieldError, PanelState, WarningNote, errorStatus, serverMessage, theadClass, thClass, useConfirm,
  type Tone, type Translator,
} from './glUi';

/**
 * Setup → GL & rates → ERP API access: the machine keys a client's ERP uses to pull journal export files and,
 * when allowed, confirm or reject them. The server owns every rule (Admin + two-step sign-in, scopes, expiry,
 * address allow-list); this panel explains them in plain words and never keeps a key's secret after showing it once.
 */

const DAY_MS = 86_400_000;
const DEFAULT_DAYS = 90;
const EXPIRY_WARNING_DAYS = 14;

type Blocked = 'mfa' | 'admin' | null;

function daysUntil(iso: string, now = Date.now()): number {
  const at = new Date(iso).getTime();
  return Number.isNaN(at) ? Number.POSITIVE_INFINITY : Math.ceil((at - now) / DAY_MS);
}

function statusLabel(t: Translator, status: string): string {
  switch (status) {
    case 'Active': return t('Active');
    case 'Expired': return t('Expired');
    case 'Revoked': return t('Revoked');
    default: return status;
  }
}

function statusTone(status: string): Tone {
  if (status === 'Active') return 'emerald';
  if (status === 'Expired') return 'amber';
  return 'slate';
}

function scopeLabel(t: Translator, scope: string): string {
  switch (scope) {
    case API_KEY_SCOPE_READ: return t('Read journal exports');
    case API_KEY_SCOPE_CONFIRM: return t('Confirm or reject journal exports');
    default: return scope;
  }
}

const IPV4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?:\/(\d{1,2}))?$/;
const IPV6 = /^[0-9a-fA-F:.]+(?:\/(\d{1,3}))?$/;

/** One address or CIDR range, IPv4 or IPv6. The server re-checks; this only catches typing slips early. */
export function isIpOrRange(value: string): boolean {
  const v4 = IPV4.exec(value);
  if (v4) return v4.slice(1, 5).every((o) => Number(o) <= 255) && (v4[5] === undefined || Number(v4[5]) <= 32);
  const v6 = IPV6.exec(value);
  if (v6 && value.includes(':')) return v6[1] === undefined || Number(v6[1]) <= 128;
  return false;
}

/** One entry per line; blank lines ignored. */
export function parseAllowList(text: string): string[] {
  return text.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
}

export function ErpApiKeysPanel({ companies }: { companies: CompanyDto[] }) {
  const t = useT();
  const f = useFormat();
  const { locale } = useLocale();
  const { isGroupScope } = useCompany();

  const [keys, setKeys] = useState<ApiKeySummary[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [blocked, setBlocked] = useState<Blocked>(null);
  const [actionError, setActionError] = useState('');
  const [notice, setNotice] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [reveal, setReveal] = useState<CreateApiKeyResponse | null>(null);
  const [downloading, setDownloading] = useState(false);
  const { confirm, dialog: confirmDialog } = useConfirm();

  /** A refusal for the whole feature replaces the list; any other failure is shown where it happened. */
  const blockedBy = (err: unknown): Blocked => {
    if (errorStatus(err) !== 403) return null;
    const reason = apiKeyRefusal(err);
    return reason === 'mfa_required' ? 'mfa' : reason === 'admin_required' ? 'admin' : null;
  };

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      setKeys(await glApiKeysApi.list());
      setBlocked(null);
    } catch (err) {
      const b = blockedBy(err);
      if (b) setBlocked(b);
      else setLoadError(serverMessage(err, t));
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { void load(); }, [load]);

  const companyName = useCallback(
    (c: CompanyDto) => (locale === 'ar' && c.legalNameAr ? c.legalNameAr : c.legalNameEn),
    [locale],
  );
  const companyLabel = (k: ApiKeySummary) => {
    if (!k.companyId) return t('All companies');
    const known = companies.find((c) => c.id === k.companyId);
    return known ? companyName(known) : k.companyName || k.companyId;
  };

  // Active keys first (soonest to expire on top), then expired and revoked ones, newest first.
  const sorted = useMemo(() => {
    const list = [...(keys ?? [])];
    return list.sort((a, b) => {
      const aActive = a.status === 'Active' ? 0 : 1;
      const bActive = b.status === 'Active' ? 0 : 1;
      if (aActive !== bActive) return aActive - bActive;
      if (aActive === 0) return a.expiresAtUtc.localeCompare(b.expiresAtUtc);
      return b.createdAtUtc.localeCompare(a.createdAtUtc);
    });
  }, [keys]);
  const expiringSoon = sorted.filter((k) => k.status === 'Active' && daysUntil(k.expiresAtUtc) <= EXPIRY_WARNING_DAYS);

  const revoke = async (k: ApiKeySummary) => {
    setActionError('');
    setNotice('');
    const ok = await confirm({
      title: t('Revoke {name}?', { name: k.name }),
      message: (
        <div className="space-y-2">
          <p>{t('Your ERP will be refused immediately the next time it uses this key. This cannot be undone.')}</p>
          <p className="text-xs text-slate-500 dark:text-slate-400">{t('To rotate, create a new key, update your ERP, then revoke the old one.')}</p>
        </div>
      ),
      confirmLabel: t('Revoke key'),
      tone: 'danger',
    });
    if (!ok) return;
    try {
      const updated = await glApiKeysApi.revoke(k.id);
      setKeys((prev) => prev?.map((x) => (x.id === k.id ? { ...x, ...updated, status: updated?.status ?? 'Revoked' } : x)) ?? prev);
      setNotice(t('{name} is revoked. Your ERP can no longer use it.', { name: k.name }));
      void load();
    } catch (err) {
      const b = blockedBy(err);
      if (b) { setBlocked(b); return; }
      if (errorStatus(err) === 404) {
        setActionError(t('This key no longer exists. The list has been refreshed.'));
        void load();
        return;
      }
      setActionError(serverMessage(err, t));
    }
  };

  const downloadDescription = async () => {
    setActionError('');
    setDownloading(true);
    try {
      saveJournalFile(await glApiKeysApi.openApi());
    } catch (err) {
      setActionError(serverMessage(err, t));
    } finally {
      setDownloading(false);
    }
  };

  const onCreated = (result: CreateApiKeyResponse) => {
    setCreateOpen(false);
    setNotice('');
    setReveal(result);
    setKeys((prev) => (prev ? [result.key, ...prev.filter((x) => x.id !== result.key.id)] : [result.key]));
    void load();
  };

  return (
    <section className="surface space-y-4 p-4" aria-labelledby="erp-api-heading">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="max-w-2xl">
          <h3 id="erp-api-heading" className="flex items-center gap-1.5 text-sm font-semibold text-slate-900 dark:text-white">
            <KeyRound className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" aria-hidden />
            {t('ERP API access')}
          </h3>
          <p className="mt-1 text-xs leading-relaxed text-slate-600 dark:text-slate-300">
            {t('An API key lets your ERP read the journal export files created here and, if you allow it, confirm or reject them. ERP access is off until you create a key, and a key can never be used to sign in or to reach anything else.')}
          </p>
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('To rotate, create a new key, update your ERP, then revoke the old one.')}</p>
        </div>
        {!blocked && (
          <div className="flex flex-wrap gap-2">
            <button type="button" className="btn-secondary h-9 text-xs" onClick={() => void downloadDescription()} disabled={downloading}>
              <Download className="h-3.5 w-3.5" aria-hidden />
              {downloading ? t('Downloading…') : t('Download API description')}
            </button>
            <button type="button" className="btn-primary h-9 text-xs" onClick={() => { setActionError(''); setCreateOpen(true); }} disabled={loading && !keys}>
              <Plus className="h-3.5 w-3.5" aria-hidden />
              {t('Create API key')}
            </button>
          </div>
        )}
      </div>

      {blocked === 'mfa' && <MfaRequired />}
      {blocked === 'admin' && (
        <p role="alert" className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-300">
          {t('Only an administrator can manage ERP API keys. Ask your administrator if your ERP needs access.')}
        </p>
      )}

      {!blocked && (
        <>
          <FieldError error={actionError} />
          {notice && (
            <p role="status" className="rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300">
              {notice}
            </p>
          )}
          {expiringSoon.length > 0 && (
            <WarningNote>
              {t('{count, plural, one {# key expires} other {# keys expire}} within 14 days. Create a replacement and update your ERP before then.', { count: expiringSoon.length })}
            </WarningNote>
          )}
          <PanelState
            loading={loading && !keys}
            error={loadError}
            onRetry={() => void load()}
            empty={sorted.length === 0}
            emptyLabel={
              <span className="flex flex-col items-center gap-3">
                <span className="font-medium text-slate-700 dark:text-slate-200">{t('No ERP API keys yet')}</span>
                <span className="max-w-md text-xs">{t('Your ERP cannot reach KynexOne until you create a key.')}</span>
                <button type="button" className="btn-primary h-9 text-xs" onClick={() => setCreateOpen(true)}>
                  <Plus className="h-3.5 w-3.5" aria-hidden />
                  {t('Create API key')}
                </button>
              </span>
            }
          >
            <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10 outline-none focus-visible:ring-2 focus-visible:ring-sapphire dark:focus-visible:ring-cyanAccent" tabIndex={0} role="group" aria-labelledby="erp-api-heading">
              <table className="w-full min-w-[860px] text-sm" aria-labelledby="erp-api-heading">
                <thead className={theadClass}>
                  <tr>
                    <th scope="col" className={thClass}>{t('Name')}</th>
                    <th scope="col" className={thClass}>{t('Key starts with')}</th>
                    <th scope="col" className={thClass}>{t('Company')}</th>
                    <th scope="col" className={thClass}>{t('What it can do')}</th>
                    <th scope="col" className={thClass}>{t('Expires')}</th>
                    <th scope="col" className={thClass}>{t('Last used')}</th>
                    <th scope="col" className={thClass}>{t('Status')}</th>
                    <th scope="col" className={thClass}>{t('Actions')}</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                  {sorted.map((k) => {
                    const active = k.status === 'Active';
                    const days = daysUntil(k.expiresAtUtc);
                    const soon = active && days <= EXPIRY_WARNING_DAYS;
                    return (
                      <tr key={k.id} data-key-id={k.id} className={active ? '' : 'bg-slate-50/60 text-slate-400 opacity-70 dark:bg-white/[0.02] dark:text-slate-500'}>
                        <td className="px-3 py-2 align-top">
                          <div className={`font-medium ${active ? 'text-slate-900 dark:text-white' : ''}`}>{k.name}</div>
                          {k.createdByName && (
                            <div className="text-[11px] text-slate-500 dark:text-slate-400">
                              {t('Created by {name} on {date}', { name: k.createdByName, date: f.date(k.createdAtUtc) })}
                            </div>
                          )}
                          {k.revokedAtUtc && (
                            <div className="text-[11px]">
                              {k.revokedByName
                                ? t('Revoked by {name} on {date}', { name: k.revokedByName, date: f.date(k.revokedAtUtc) })
                                : t('Revoked on {date}', { date: f.date(k.revokedAtUtc) })}
                            </div>
                          )}
                        </td>
                        <td className="px-3 py-2 align-top">
                          <code dir="ltr" className="whitespace-nowrap font-mono text-xs">{k.prefix}…</code>
                        </td>
                        <td className="px-3 py-2 align-top">{companyLabel(k)}</td>
                        <td className="px-3 py-2 align-top">
                          <ul className="space-y-0.5 text-xs">
                            {k.scopes.map((s) => <li key={s}>{scopeLabel(t, s)}</li>)}
                          </ul>
                          {k.ipAllowList.length > 0 && (
                            <div className="mt-1 text-[11px] text-slate-500 dark:text-slate-400">
                              {t('Only from: {addresses}', { addresses: k.ipAllowList.join(', ') })}
                            </div>
                          )}
                        </td>
                        <td className="px-3 py-2 align-top whitespace-nowrap">
                          <div>{f.date(k.expiresAtUtc)}</div>
                          {soon && (
                            <div className="text-[11px] font-semibold text-amber-700 dark:text-amber-300">
                              {days <= 0
                                ? t('Expires today')
                                : t('{count, plural, one {Expires in # day} other {Expires in # days}}', { count: days })}
                            </div>
                          )}
                        </td>
                        <td className="px-3 py-2 align-top text-xs">
                          {k.lastUsedAtUtc
                            ? k.lastUsedIp
                              ? t('{date} from {address}', { date: f.dateTime(k.lastUsedAtUtc), address: k.lastUsedIp })
                              : f.dateTime(k.lastUsedAtUtc)
                            : t('Never used')}
                        </td>
                        <td className="px-3 py-2 align-top">
                          <Badge tone={statusTone(k.status)}>{statusLabel(t, k.status)}</Badge>
                        </td>
                        <td className="px-3 py-2 align-top">
                          {active ? (
                            <button
                              type="button"
                              onClick={() => void revoke(k)}
                              aria-label={t('Revoke {name}', { name: k.name })}
                              className="whitespace-nowrap rounded-md px-2 py-1 text-xs font-semibold text-rose-700 hover:bg-rose-50 dark:text-rose-300 dark:hover:bg-rose-500/10"
                            >
                              {t('Revoke')}
                            </button>
                          ) : (
                            <span className="text-xs">—</span>
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </PanelState>
        </>
      )}

      {createOpen && (
        <CreateKeyDialog
          companies={companies}
          companyName={companyName}
          isGroupScope={isGroupScope}
          onClose={() => setCreateOpen(false)}
          onCreated={onCreated}
          onBlocked={(b) => { setCreateOpen(false); setBlocked(b); }}
          blockedBy={blockedBy}
        />
      )}
      {reveal && <RevealKeyDialog result={reveal} onDone={() => setReveal(null)} />}
      {confirmDialog}
    </section>
  );
}

function MfaRequired() {
  const t = useT();
  const router = useRouter();
  const { beginMfaEnrollment } = useAuth();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  // The same hand-off as the app-wide two-step prompt: the sign-in page runs the enrolment step.
  const start = async () => {
    setBusy(true);
    setError('');
    try {
      await beginMfaEnrollment();
      router.replace('/login');
    } catch (err) {
      setError(serverMessage(err, t));
      setBusy(false);
    }
  };

  return (
    <div role="alert" className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
      <p className="flex items-center gap-1.5 font-semibold">
        <ShieldCheck className="h-4 w-4 shrink-0" aria-hidden />
        {t('Turn on two-step sign-in to manage ERP API keys')}
      </p>
      <p className="mt-1 text-xs leading-relaxed">
        {t('An API key gives a machine access to your ledger, so only an administrator with two-step sign-in can create or revoke one. Setting it up takes about a minute; you will sign in again afterwards, then come back here.')}
      </p>
      {error && <p className="mt-1 text-xs font-medium text-rose-700 dark:text-rose-300">{error}</p>}
      <button type="button" onClick={() => void start()} disabled={busy} className="btn-primary mt-3 h-9 text-xs">
        {busy ? t('Starting…') : t('Set up two-step sign-in')}
      </button>
    </div>
  );
}

function CreateKeyDialog({
  companies,
  companyName,
  isGroupScope,
  onClose,
  onCreated,
  onBlocked,
  blockedBy,
}: {
  companies: CompanyDto[];
  companyName: (c: CompanyDto) => string;
  isGroupScope: boolean;
  onClose: () => void;
  onCreated: (result: CreateApiKeyResponse) => void;
  onBlocked: (b: Exclude<Blocked, null>) => void;
  blockedBy: (err: unknown) => Blocked;
}) {
  const t = useT();
  const f = useFormat();
  const [name, setName] = useState('');
  const [companyId, setCompanyId] = useState<string>(isGroupScope ? '' : companies[0]?.id ?? '');
  const [canRead, setCanRead] = useState(true);
  const [canConfirm, setCanConfirm] = useState(false);
  const [days, setDays] = useState(String(DEFAULT_DAYS));
  const [ipText, setIpText] = useState('');
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!isGroupScope && !companyId && companies.length > 0) setCompanyId(companies[0].id);
  }, [isGroupScope, companyId, companies]);

  const dayCount = Number(days);
  const daysValid = Number.isInteger(dayCount) && dayCount >= 1 && dayCount <= API_KEY_MAX_DAYS;
  const expiresAt = daysValid ? new Date(Date.now() + dayCount * DAY_MS) : null;

  const validate = (): string | null => {
    if (!name.trim()) return t('Enter a name for the key.');
    if (!isGroupScope && !companyId) return t('Choose the company this key is for.');
    if (!canRead) return t('A key must be able to read journal exports.');
    if (!daysValid) return t('Choose an expiry between 1 and {max} days.', { max: API_KEY_MAX_DAYS });
    const bad = parseAllowList(ipText).find((x) => !isIpOrRange(x));
    if (bad) return t('{value} is not an IP address or range. Put one address or range on each line, for example 203.0.113.0/24.', { value: bad });
    return null;
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const problem = validate();
    if (problem) { setError(problem); return; }
    setError('');
    setSaving(true);
    const scopes: ApiKeyScope[] = [API_KEY_SCOPE_READ, ...(canConfirm ? [API_KEY_SCOPE_CONFIRM] : [])];
    try {
      const result = await glApiKeysApi.create({
        name: name.trim(),
        companyId: companyId || null,
        scopes,
        expiresAtUtc: new Date(Date.now() + dayCount * DAY_MS).toISOString(),
        ipAllowList: parseAllowList(ipText),
      });
      onCreated(result);
    } catch (err) {
      const b = blockedBy(err);
      if (b) { onBlocked(b); return; }
      setError(serverMessage(err, t));
      setSaving(false);
    }
  };

  return (
    <Modal
      isOpen
      title={t('Create ERP API key')}
      onClose={onClose}
      size="md"
      footer={
        <>
          <button type="button" className="btn-secondary" onClick={onClose} disabled={saving}>{t('Cancel')}</button>
          <button type="submit" form="erp-api-key-form" className="btn-primary" disabled={saving}>
            {saving ? t('Creating…') : t('Create key')}
          </button>
        </>
      }
    >
      <form id="erp-api-key-form" className="space-y-4" onSubmit={(e) => void submit(e)} noValidate>
        <FieldError error={error} />
        <Field label={t('Key name')} required hint={t('A name your team will recognise, for example "SAP production".')}>
          <input className="input w-full" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} autoComplete="off" />
        </Field>
        <Field label={t('Company')} required hint={t('The key only reaches journal exports for this company.')}>
          <select className="select w-full" value={companyId} onChange={(e) => setCompanyId(e.target.value)}>
            {isGroupScope && <option value="">{t('All companies')}</option>}
            {companies.map((c) => <option key={c.id} value={c.id}>{companyName(c)}</option>)}
          </select>
        </Field>
        <fieldset>
          <legend className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">{t('What your ERP may do')}</legend>
          <label className="flex items-start gap-2 text-sm text-slate-700 dark:text-slate-300">
            <input type="checkbox" className="mt-0.5" checked={canRead} onChange={(e) => setCanRead(e.target.checked)} />
            <span>
              {t('Read journal exports')}
              <span className="block text-[11px] text-slate-500 dark:text-slate-400">{t('Required. Lists and downloads the journal files created here.')}</span>
            </span>
          </label>
          <label className="mt-2 flex items-start gap-2 text-sm text-slate-700 dark:text-slate-300">
            <input type="checkbox" className="mt-0.5" checked={canConfirm} onChange={(e) => setCanConfirm(e.target.checked)} />
            <span>
              {t('Confirm or reject journal exports')}
              <span className="block text-[11px] text-slate-500 dark:text-slate-400">{t('Optional. Lets your ERP record that it posted a file, or why it refused it.')}</span>
            </span>
          </label>
        </fieldset>
        <Field
          label={t('Expires after (days)')}
          required
          hint={expiresAt ? t('The key stops working on {date}.', { date: f.date(expiresAt) }) : t('Between 1 and {max} days.', { max: API_KEY_MAX_DAYS })}
        >
          <input type="number" inputMode="numeric" className="input w-32" min={1} max={API_KEY_MAX_DAYS} step={1} value={days} onChange={(e) => setDays(e.target.value)} />
        </Field>
        <Field label={t('Allowed addresses (optional)')} hint={t('One address or range per line. Leave empty to allow any address. Example: 203.0.113.0/24')}>
          <textarea dir="ltr" className="input min-h-[84px] w-full font-mono text-xs" value={ipText} onChange={(e) => setIpText(e.target.value)} spellCheck={false} />
        </Field>
      </form>
    </Modal>
  );
}

function RevealKeyDialog({ result, onDone }: { result: CreateApiKeyResponse; onDone: () => void }) {
  const t = useT();
  const [copied, setCopied] = useState<'' | 'yes' | 'failed'>('');

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(result.plaintext);
      setCopied('yes');
    } catch {
      setCopied('failed');
    }
  };

  return (
    <Modal
      isOpen
      title={t('Your new ERP API key')}
      onClose={onDone}
      size="md"
      footer={<button type="button" className="btn-primary" onClick={onDone}>{t('I have stored the key')}</button>}
    >
      <div className="space-y-3">
        <WarningNote>
          <strong>{t('This is the only time the key is shown. Store it in your ERP now. If you lose it, revoke it and create a new one.')}</strong>
        </WarningNote>
        <Field label={t('API key for {name}', { name: result.key.name })}>
          <input
            readOnly
            dir="ltr"
            className="input w-full font-mono text-xs"
            value={result.plaintext}
            onFocus={(e) => e.currentTarget.select()}
            data-testid="erp-api-key-plaintext"
          />
        </Field>
        <div className="flex flex-wrap items-center gap-2">
          <button type="button" className="btn-secondary h-9 text-xs" onClick={() => void copy()}>{t('Copy key')}</button>
          <span role="status" className="text-xs font-medium text-emerald-700 dark:text-emerald-300">
            {copied === 'yes' ? t('Copied') : ''}
          </span>
          {copied === 'failed' && (
            <span role="alert" className="text-xs text-rose-700 dark:text-rose-300">{t('Copy failed. Select the key and copy it yourself.')}</span>
          )}
        </div>
      </div>
    </Modal>
  );
}
