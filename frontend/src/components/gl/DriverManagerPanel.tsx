'use client';

import { useCallback, useMemo, useState } from 'react';
import { Pencil, Plus } from 'lucide-react';
import { notifyApiError } from '../../api/client';
import {
  financeGlApi,
  GL_ACCOUNT_TYPES,
  GL_DRIVER_CATEGORIES,
  GL_DRIVER_MATCH_MODES,
  type GlAccount,
  type GlDriver,
  type GlDriverRequest,
  type GlResolveRow,
} from '../../api/financeGl';
import { useT } from '../../hooks/useT';
import { useAppToast } from '../ui/AppToast';
import { Modal } from '../Modal';
import {
  accountTypeLabel,
  Badge,
  driverCategoryLabel,
  Field,
  FieldError,
  InheritedBadge,
  PanelState,
  routeSourceLabel,
  serverMessage,
  sideLabel,
  SystemBadge,
  theadClass,
  thClass,
  useConfirm,
  useScopedResource,
  WarningNote,
  type Translator,
} from './glUi';

interface Props {
  scope: string | null;
  scopeName: string;
  canManage: boolean; // finance.gl.drivers.manage
  canAuthorPredicates: boolean; // finance.gl.drivers.author_predicates
  /** Effective routing from GET /gl/resolve, when the caller could read it. */
  resolveRows: GlResolveRow[] | null;
  onRoutingChanged: () => void;
}

const MATCH_SOURCES = ['Bonus', 'Statutory', 'Tax', 'Loan', 'Attendance', 'Leave'] as const;
/** A machine key, shown as an input example; not translatable copy. */
const KEY_EXAMPLE = 'EARN:WELLNESS';

function matchModeLabel(t: Translator, mode: string): string {
  switch (mode) {
    case 'Exact': return t('Exact code');
    case 'Suffix': return t('Code ends with');
    case 'Prefix': return t('Code starts with');
    case 'Any': return t('Any line in the category');
    default: return mode;
  }
}

function matchSourceLabel(t: Translator, source: string): string {
  switch (source) {
    case 'Bonus': return t('Bonus');
    case 'Statutory': return t('Statutory');
    case 'Tax': return t('Tax');
    case 'Loan': return t('Loan');
    case 'Attendance': return t('Attendance');
    case 'Leave': return t('Leave');
    default: return source;
  }
}

const emptyDraft = (): GlDriverRequest => ({
  key: '',
  label: '',
  category: 'Earning',
  postingSide: 'DR',
  accountType: 'Expense',
  defaultCode: '',
  defaultName: '',
  matchSource: '',
  matchMode: 'Exact',
  matchComponentCode: '',
  emitsEmployerExpensePair: false,
  pairedExpenseDriverKey: '',
  sortOrder: 500,
  isActive: true,
});

interface Loaded {
  drivers: GlDriver[];
  accounts: GlAccount[];
}

export function DriverManagerPanel({ scope, scopeName, canManage, canAuthorPredicates, resolveRows, onRoutingChanged }: Props) {
  const t = useT();
  const toast = useAppToast();
  const { confirm, dialog } = useConfirm();
  const inCompanyView = scope !== null;

  // Drivers are the panel; chart accounts only feed the fallback-account picker, so their failure
  // must not hide the drivers.
  const fetcher = useCallback(async (): Promise<Loaded> => {
    const [drivers, own, group] = await Promise.all([
      financeGlApi.listDrivers(scope),
      financeGlApi.listAccounts(scope).catch(() => [] as GlAccount[]),
      scope ? financeGlApi.listAccounts(null).catch(() => [] as GlAccount[]) : Promise.resolve([] as GlAccount[]),
    ]);
    const byId = new Map<string, GlAccount>();
    for (const a of [...own, ...group]) if (a.companyId === null || a.companyId === scope) byId.set(a.id, a);
    return { drivers, accounts: [...byId.values()] };
  }, [scope]);
  const res = useScopedResource<Loaded>(`drivers:${scope ?? 'group'}`, fetcher);
  const drivers = useMemo(() => res.data?.drivers ?? [], [res.data]);
  const accounts = useMemo(
    () => [...(res.data?.accounts ?? [])].sort((a, b) => a.code.localeCompare(b.code, undefined, { numeric: true })),
    [res.data],
  );
  const routeByKey = useMemo(() => new Map((resolveRows ?? []).map((r) => [r.driverKey, r])), [resolveRows]);

  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<GlDriver | null>(null);
  const [draft, setDraft] = useState<GlDriverRequest>(emptyDraft());
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  // A driver is editable in this scope only if owned by it (system + group-custom in group view;
  // company-custom in a company view). Inherited rows are read-only.
  const ownedHere = (d: GlDriver) => d.companyId === scope;

  const systemDrDrivers = useMemo(() => drivers.filter((d) => d.postingSide === 'DR' && d.isSystem), [drivers]);

  const setField = <K extends keyof GlDriverRequest>(k: K, v: GlDriverRequest[K]) => setDraft((d) => ({ ...d, [k]: v }));

  const openCreate = () => {
    setEditing(null);
    setDraft(emptyDraft());
    setError('');
    setModalOpen(true);
  };

  const openEdit = (d: GlDriver) => {
    setEditing(d);
    setDraft({
      key: d.key,
      label: d.label,
      category: d.category,
      postingSide: d.postingSide,
      accountType: d.accountType,
      defaultCode: d.defaultCode,
      defaultName: d.defaultName,
      matchSource: d.matchSource ?? '',
      matchMode: d.matchMode,
      matchComponentCode: d.matchComponentCode ?? '',
      emitsEmployerExpensePair: d.emitsEmployerExpensePair,
      pairedExpenseDriverKey: d.pairedExpenseDriverKey ?? '',
      sortOrder: d.sortOrder,
      isActive: d.isActive,
    });
    setError('');
    setModalOpen(true);
  };

  // Fallback account picker: the chart's accounts, plus the driver's current fallback when it is not in the chart.
  const fallbackInChart = accounts.some((a) => a.code === draft.defaultCode);
  const pickFallback = (code: string) => {
    const a = accounts.find((x) => x.code === code);
    setDraft((d) => ({ ...d, defaultCode: code, defaultName: a?.name ?? d.defaultName }));
  };

  const validateDraft = (): string | null => {
    if (!draft.label.trim()) return t('Enter a name for the driver.');
    if (!draft.defaultCode.trim()) return t('Choose a fallback account.');
    if (editing?.isSystem) return null; // system: routing is locked; the server enforces the rest
    if (!draft.key.trim()) return t('Enter a key.');
    const mode = draft.matchMode ?? 'Exact';
    const hasCode = !!draft.matchComponentCode?.trim();
    if (mode === 'Any' && hasCode) return t('"Any line in the category" must not carry a pay component code.');
    if (mode !== 'Any' && !hasCode) return t('Enter the pay component code to match.');
    if (draft.emitsEmployerExpensePair && !draft.pairedExpenseDriverKey?.trim())
      return t('Choose the paired employer-expense driver.');
    return null;
  };

  const save = async () => {
    const v = validateDraft();
    if (v) {
      setError(v);
      return;
    }
    if (editing && editing.isActive && !draft.isActive) {
      const ok = await confirm({
        title: t('Deactivate {driver}?', { driver: editing.label || editing.key }),
        message: t('Pay lines this driver routes today will no longer match it. They fall to a broader driver or post with no account until you map them again.'),
        confirmLabel: t('Deactivate'),
        tone: 'danger',
      });
      if (!ok) return;
    }
    setSaving(true);
    setError('');
    try {
      if (editing) {
        await financeGlApi.updateDriver(editing.id, draft);
        setModalOpen(false);
        toast.success(t('Driver saved.'));
      } else {
        const r = await financeGlApi.createDriver(draft, scope);
        setModalOpen(false);
        // A shadowed driver is saved; the server warns that a more specific driver wins for some lines.
        if (r.warning) toast.warn(r.warning, t('Driver saved with a warning'));
        else toast.success(t('Driver added.'));
      }
      await res.reload();
      onRoutingChanged();
    } catch (err) {
      setError(serverMessage(err, t));
    } finally {
      setSaving(false);
    }
  };

  const remove = async (d: GlDriver) => {
    const ok = await confirm({
      title: t('Delete driver {key}?', { key: d.key }),
      message: t('Pay lines it routes will fall to a broader driver. This cannot be undone.'),
      confirmLabel: t('Delete'),
      tone: 'danger',
    });
    if (!ok) return;
    setDeletingId(d.id);
    try {
      await financeGlApi.deleteDriver(d.id);
      toast.success(t('Driver deleted.'));
      await res.reload();
      onRoutingChanged();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setDeletingId(null);
    }
  };

  const mode = draft.matchMode ?? 'Exact';
  const isSystemEdit = !!editing?.isSystem;
  // Non-Admin authors are restricted to Exact-code drivers with no employer-expense pairing.
  const predicateLocked = !canAuthorPredicates && !isSystemEdit;
  const writable = canManage && res.ready;

  return (
    <div className="surface p-4">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div>
          <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Posting Drivers')}</h3>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('How payroll lines route to GL accounts for {scope}.', { scope: scopeName })}{' '}
            {t("System drivers' routing rules are locked; you can rename them and change their fallback account.")}
          </p>
        </div>
        {canManage && (
          <button type="button" onClick={openCreate} disabled={!writable} className="btn-primary text-xs disabled:opacity-60">
            <Plus className="h-3.5 w-3.5" aria-hidden /> {t('Add driver')}
          </button>
        )}
      </div>

      <PanelState
        loading={res.loading && !res.data}
        error={res.error}
        onRetry={() => void res.reload()}
        empty={drivers.length === 0}
        emptyLabel={t('No posting drivers are set up yet. Load the standard payroll chart from the Accounts & Mapping tab.')}
      >
        <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10">
          <table className="w-full min-w-[900px] text-sm">
            <thead className={theadClass}>
              <tr>
                <th scope="col" className={thClass}>{t('Driver')}</th>
                <th scope="col" className={thClass}>{t('Category')}</th>
                <th scope="col" className={thClass}>{t('Side')}</th>
                <th scope="col" className={thClass}>{t('Matches')}</th>
                <th scope="col" className={thClass}>{t('Fallback account')}</th>
                <th scope="col" className={thClass}>{t('Effective account')}</th>
                <th scope="col" className={`${thClass} text-end`}>{t('Actions')}</th>
              </tr>
            </thead>
            <tbody>
              {drivers.map((d) => {
                // A non-predicate-author may only edit system drivers (limited fields) and their own
                // Exact, non-paired custom drivers — never a predicate-authored one they can't re-save.
                const editable =
                  canManage && ownedHere(d) && (d.isSystem || canAuthorPredicates || (d.matchMode === 'Exact' && !d.emitsEmployerExpensePair));
                const route = routeByKey.get(d.key);
                const src = route ? routeSourceLabel(t, route.source) : null;
                return (
                  <tr key={d.id} className={`border-t border-slate-100 dark:border-white/5 ${d.isActive ? '' : 'text-slate-400'}`}>
                    <td className="px-3 py-2">
                      <div className="flex flex-wrap items-center gap-1.5">
                        <span className="text-slate-800 dark:text-slate-100">{d.label || d.key}</span>
                        {d.isSystem && <SystemBadge />}
                        {inCompanyView && d.companyId === null && <InheritedBadge />}
                        {inCompanyView && d.companyId === scope && <Badge tone="violet">{t('Company')}</Badge>}
                        {!d.isActive && <Badge tone="slate">{t('Inactive')}</Badge>}
                        {d.emitsEmployerExpensePair && (
                          <Badge tone="amber" title={t('Also posts a paired employer-cost debit to {driver}.', { driver: d.pairedExpenseDriverKey ?? '' })}>
                            {t('Employer cost pair')}
                          </Badge>
                        )}
                      </div>
                      <p className="text-[11px] text-slate-500"><span className="font-mono">{d.key}</span></p>
                    </td>
                    <td className="px-3 py-2 text-xs text-slate-600 dark:text-slate-300">{driverCategoryLabel(t, d.category)}</td>
                    <td className="px-3 py-2 text-xs text-slate-600 dark:text-slate-300">{sideLabel(t, d.postingSide)}</td>
                    <td className="px-3 py-2 text-xs text-slate-600 dark:text-slate-300">
                      {matchModeLabel(t, d.matchMode)}
                      {d.matchMode !== 'Any' && d.matchComponentCode && <span className="ms-1 font-mono">{d.matchComponentCode}</span>}
                      {d.matchSource && <span className="block text-[11px] text-slate-500">{t('Source {source}', { source: matchSourceLabel(t, d.matchSource) })}</span>}
                    </td>
                    <td className="px-3 py-2 text-xs">
                      <span className="font-mono">{d.defaultCode}</span>
                      <span className="ms-1 text-slate-600 dark:text-slate-300">{d.defaultName}</span>
                    </td>
                    <td className="px-3 py-2 text-xs">
                      {route ? (
                        <div className="space-y-0.5">
                          <div>
                            <span className="font-mono">{route.account?.code || '—'}</span>
                            {route.account?.name && <span className="ms-1 text-slate-600 dark:text-slate-300">{route.account.name}</span>}
                          </div>
                          {src && <Badge tone={src.tone} title={src.hint}>{src.label}</Badge>}
                        </div>
                      ) : (
                        <span className="text-slate-400">—</span>
                      )}
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex items-center justify-end gap-1.5">
                        {editable ? (
                          <>
                            <button type="button" onClick={() => openEdit(d)} disabled={!writable} className="btn-secondary h-7 whitespace-nowrap px-2 text-xs disabled:opacity-60">
                              <Pencil className="h-3 w-3" aria-hidden /> {d.isSystem ? t('Edit label & fallback') : t('Edit')}
                            </button>
                            {!d.isSystem && (
                              <button
                                type="button"
                                onClick={() => remove(d)}
                                disabled={!writable || deletingId === d.id}
                                aria-label={t('Delete driver {key}', { key: d.key })}
                                className="h-7 rounded-md border border-slate-200 px-2 text-xs font-medium text-rose-600 hover:border-rose-300 hover:bg-rose-50 disabled:opacity-40 dark:border-white/10 dark:text-rose-400"
                              >
                                {t('Delete')}
                              </button>
                            )}
                          </>
                        ) : (
                          <span className="text-[11px] text-slate-400" aria-hidden>—</span>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </PanelState>

      {/* Create / edit driver modal */}
      <Modal
        isOpen={modalOpen}
        title={editing ? (isSystemEdit ? t('Edit label & fallback: {key}', { key: editing.key }) : t('Edit driver {key}', { key: editing.key })) : t('Add custom driver')}
        onClose={() => setModalOpen(false)}
        size="lg"
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">
              {t('Cancel')}
            </button>
            <button type="button" onClick={save} disabled={saving || !writable} className="btn-primary disabled:opacity-60">
              {saving ? t('Saving…') : t('Save')}
            </button>
          </>
        }
      >
        <FieldError error={error} />
        {isSystemEdit && (
          <p className="mb-3 rounded-lg bg-sapphire/10 px-3 py-2 text-xs text-sapphire dark:bg-sapphire/15 dark:text-cyanAccent">
            {t("System drivers' routing rules are locked; you can rename them and change their fallback account.")}
          </p>
        )}
        {predicateLocked && !editing && (
          <WarningNote>
            {t('You can add drivers that match one exact pay component code. Wider matches and employer-cost pairs need the "author routing rules" permission.')}
          </WarningNote>
        )}

        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          {!isSystemEdit && (
            <Field label={t('Key')} required hint={t('Unique, for example EARN:WELLNESS. A system key cannot be reused.')}>
              <input value={draft.key} onChange={(e) => setField('key', e.target.value)} className="input w-full font-mono" placeholder={KEY_EXAMPLE} />
            </Field>
          )}
          <Field label={t('Name')} required>
            <input value={draft.label} onChange={(e) => setField('label', e.target.value)} className="input w-full" placeholder={t('Wellness allowance')} />
          </Field>

          <Field
            label={t('Fallback account')}
            required
            hint={
              accounts.length === 0
                ? t('The chart of accounts is empty. Add the account there first, then pick it here.')
                : t('Used when no mapping applies for a company. Pick from the chart of accounts.')
            }
          >
            <select value={draft.defaultCode} onChange={(e) => pickFallback(e.target.value)} className="select w-full">
              <option value="">{t('Choose an account…')}</option>
              {draft.defaultCode && !fallbackInChart && (
                <option value={draft.defaultCode}>
                  {t('{code} — {name} (not in the chart)', { code: draft.defaultCode, name: draft.defaultName })}
                </option>
              )}
              {accounts.map((a) => (
                <option key={a.id} value={a.code} disabled={!a.isActive && a.code !== draft.defaultCode}>
                  {a.isActive ? `${a.code} — ${a.name}` : t('{code} — {name} (inactive)', { code: a.code, name: a.name })}
                </option>
              ))}
            </select>
          </Field>
          {editing && routeByKey.get(editing.key) && (
            <Field label={t('Effective account')}>
              <input
                readOnly
                value={`${routeByKey.get(editing.key)!.account?.code ?? ''} — ${routeByKey.get(editing.key)!.account?.name ?? ''}`}
                className="input w-full opacity-70"
              />
            </Field>
          )}

          {!isSystemEdit && (
            <>
              <Field label={t('Category')} required>
                <select value={draft.category} onChange={(e) => setField('category', e.target.value)} className="select w-full">
                  {GL_DRIVER_CATEGORIES.map((c) => (
                    <option key={c} value={c}>
                      {driverCategoryLabel(t, c)}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label={t('Posting side')} required>
                <select value={draft.postingSide} onChange={(e) => setField('postingSide', e.target.value)} className="select w-full">
                  <option value="DR">{t('Debit')}</option>
                  <option value="CR">{t('Credit')}</option>
                </select>
              </Field>
              <Field label={t('Account type')}>
                <select value={draft.accountType} onChange={(e) => setField('accountType', e.target.value)} className="select w-full">
                  {GL_ACCOUNT_TYPES.map((type) => (
                    <option key={type} value={type}>
                      {accountTypeLabel(t, type)}
                    </option>
                  ))}
                </select>
              </Field>
            </>
          )}
          <Field label={t('Sort order')} hint={t('Lower sorts earlier; breaks ties between equally specific drivers.')}>
            <input type="number" value={draft.sortOrder} onChange={(e) => setField('sortOrder', Number(e.target.value))} className="input w-full" />
          </Field>

          {!isSystemEdit && (
            <>
              <Field label={t('Match')} hint={t('Exact code matches one pay component; "any line" catches the whole category.')}>
                <select value={mode} disabled={predicateLocked} onChange={(e) => setField('matchMode', e.target.value)} className="select w-full disabled:opacity-60">
                  {GL_DRIVER_MATCH_MODES.map((m) => (
                    <option key={m} value={m} disabled={predicateLocked && m !== 'Exact'}>
                      {matchModeLabel(t, m)}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label={t('Pay component code')} hint={mode === 'Any' ? t('Not used when matching any line.') : t('The pay component code to match.')}>
                <input
                  value={draft.matchComponentCode ?? ''}
                  disabled={mode === 'Any'}
                  onChange={(e) => setField('matchComponentCode', e.target.value)}
                  className="input w-full font-mono disabled:opacity-60"
                  placeholder="WELLNESS"
                />
              </Field>
              <Field label={t('Line source')} hint={t('Optional: only match lines from this source.')}>
                <select value={draft.matchSource ?? ''} disabled={predicateLocked} onChange={(e) => setField('matchSource', e.target.value)} className="select w-full disabled:opacity-60">
                  <option value="">{t('Any source')}</option>
                  {MATCH_SOURCES.map((s) => (
                    <option key={s} value={s}>
                      {matchSourceLabel(t, s)}
                    </option>
                  ))}
                </select>
              </Field>
            </>
          )}
          {/* A system driver cannot be deactivated (the server refuses it), so it is not offered. */}
          {!isSystemEdit && (
            <Field label={t('Status')}>
              <select value={draft.isActive ? 'true' : 'false'} onChange={(e) => setField('isActive', e.target.value === 'true')} className="select w-full">
                <option value="true">{t('Active')}</option>
                <option value="false">{t('Inactive')}</option>
              </select>
            </Field>
          )}

          {!predicateLocked && !isSystemEdit && (
            <>
              <Field label={t('Employer cost pair')} hint={t('Also posts a paired employer-cost debit (statutory employer cost).')}>
                <select value={draft.emitsEmployerExpensePair ? 'true' : 'false'} onChange={(e) => setField('emitsEmployerExpensePair', e.target.value === 'true')} className="select w-full">
                  <option value="false">{t('No')}</option>
                  <option value="true">{t('Yes')}</option>
                </select>
              </Field>
              {draft.emitsEmployerExpensePair && (
                <Field label={t('Paired expense driver')} required hint={t('Must be a system debit balancing driver.')}>
                  <select value={draft.pairedExpenseDriverKey ?? ''} onChange={(e) => setField('pairedExpenseDriverKey', e.target.value)} className="select w-full">
                    <option value="">{t('Choose a driver…')}</option>
                    {systemDrDrivers.map((d) => (
                      <option key={d.key} value={d.key}>
                        {d.label ? `${d.label} (${d.key})` : d.key}
                      </option>
                    ))}
                  </select>
                </Field>
              )}
            </>
          )}
        </div>
      </Modal>

      {dialog}
    </div>
  );
}
