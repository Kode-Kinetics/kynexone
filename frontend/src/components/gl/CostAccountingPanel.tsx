'use client';

import Link from 'next/link';
import { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, ChevronDown, ChevronRight, Info, OctagonAlert, RotateCcw } from 'lucide-react';
import { financeGlApi, type CostAccountingReadiness } from '../../api/financeGl';
import type { CompanyDto, CostCenterDto } from '../../api/organization';
import { useLocale } from '../../contexts/LocaleContext';
import { useT } from '../../hooks/useT';
import {
  checkFixTarget,
  fixedCostCentreOptions,
  orderChecks,
  readinessAllClear,
  sampleHref,
  blockerFixLinks,
  blockerReason,
  type BlockerLevel,
  textDir,
  turnOnGate,
} from '../../lib/glCostAccounting';
import { Modal } from '../Modal';
import { useAppToast } from '../ui/AppToast';
import { Field, FieldError, LoadError, Spinner, serverMessage, useScopedResource, type Translator } from './glUi';

interface Props {
  companies: CompanyDto[];
  /** The panel's configuration scope: a company guid, or null for the group view (then a company is picked here). */
  scope: string | null;
  costCenters: CostCenterDto[];
  canManage: boolean;
  /** Opens another Payroll accounting tab (the account rules, for "Set rules"). */
  onOpenTab: (tab: 'coa') => void;
}

function checkLabel(t: Translator, key: string): string {
  switch (key) {
    case 'employees-unresolved': return t('Employees with no cost centre');
    case 'invalid-reference': return t('Positions, departments or employees with an invalid cost centre');
    case 'accounts-no-rule': return t('Expense accounts with no cost centre rule');
    case 'no-company': return t('Cost centres with no company');
    case 'rounding-default': return t('The rounding account requires a cost centre but no default is set');
    case 'mapping-invisible': return t('A posting rule uses a cost centre this company cannot see');
    case 'employees-no-company': return t('Active employees with no company');
    case 'currency-scale': return t('The company currency uses three decimal places, which payroll cannot carry yet');
    case 'small-cell': return t('Cost centres with very few employees');
    case 'rules-not-enforced': return t('Required rules that nothing enforces yet');
    default: return key;
  }
}

/** Why one employee has no cost centre (AM-1): none at any level, or the level whose cost centre cannot be used. */
function blockerReasonText(t: Translator, level: BlockerLevel): string {
  switch (level) {
    case 'none': return t('No cost centre at any level.');
    case 'employee': return t('Its own cost centre is inactive, deleted or in another company.');
    case 'position': return t("Its position's cost centre is inactive, deleted or in another company.");
    case 'department': return t("Its department's cost centre is inactive, deleted or in another company.");
    case 'parentDepartment': return t("Its parent department's cost centre is inactive, deleted or in another company.");
    default: return t('The company default cost centre is inactive, deleted or in another company.');
  }
}

function fixLabel(t: Translator, key: string): string {
  return key === 'accounts-no-rule' || key === 'rules-not-enforced' ? t('Set rules') : key === 'rounding-default' ? t('Set default') : t('Fix');
}

function SeverityMark({ severity }: { severity: string }) {
  const t = useT();
  if (severity === 'blocker') {
    return <span className="inline-flex items-center gap-1 text-xs font-semibold text-rose-700 dark:text-rose-300"><OctagonAlert className="h-3.5 w-3.5" aria-hidden />{t('Blocker')}</span>;
  }
  if (severity === 'warning') {
    return <span className="inline-flex items-center gap-1 text-xs font-semibold text-amber-800 dark:text-amber-300"><AlertTriangle className="h-3.5 w-3.5" aria-hidden />{t('Warning')}</span>;
  }
  return <span className="inline-flex items-center gap-1 text-xs font-semibold text-emerald-700 dark:text-emerald-300"><CheckCircle2 className="h-3.5 w-3.5" aria-hidden />{t('OK')}</span>;
}

export function CostAccountingPanel({ companies, scope, costCenters, canManage, onOpenTab }: Props) {
  const t = useT();
  const { locale } = useLocale();
  const toast = useAppToast();

  // Cost accounting is per company: in the group view the company is chosen here.
  const [picked, setPicked] = useState<string | null>(companies[0]?.id ?? null);
  useEffect(() => {
    if (!companies.some((c) => c.id === picked)) setPicked(companies[0]?.id ?? null);
  }, [companies, picked]);
  const companyId = scope ?? picked;
  const company = companies.find((c) => c.id === companyId) ?? null;
  const companyName = company ? (locale === 'ar' && company.legalNameAr ? company.legalNameAr : company.legalNameEn) : '';

  const res = useScopedResource<CostAccountingReadiness>(`ready:${companyId}`, () => financeGlApi.costAccountingReadiness(companyId!), !!companyId);
  const data = res.data;
  const settings = data?.settings;
  const enabled = !!settings?.enabled;
  const awaiting = !!settings?.awaitingRun;
  const m7 = t('A payroll run for this company is awaiting finance review or lock, and this change would alter what it posts. Lock the run, or reopen it first.');
  const canChange = canManage && !awaiting && res.ready;

  // ── Default cost centre ──
  const defaultOptions = useMemo(
    () => [...fixedCostCentreOptions(costCenters, companyId)].sort((a, b) => a.code.localeCompare(b.code, undefined, { numeric: true })),
    [costCenters, companyId],
  );
  const [defaultId, setDefaultId] = useState('');
  const [defaultReason, setDefaultReason] = useState('');
  const [defaultBusy, setDefaultBusy] = useState(false);
  const [defaultError, setDefaultError] = useState('');
  const currentDefault = settings?.defaultCostCenterId ?? '';
  useEffect(() => {
    setDefaultId(currentDefault);
    setDefaultReason('');
    setDefaultError('');
  }, [currentDefault, companyId]);
  const defaultChanged = defaultId !== currentDefault;
  const saveDefault = async () => {
    if (!companyId || !defaultReason.trim()) return;
    setDefaultBusy(true);
    setDefaultError('');
    try {
      await financeGlApi.setCostAccounting(companyId, defaultId
        ? { defaultCostCenterId: defaultId, reason: defaultReason.trim() }
        : { clearDefaultCostCenter: true, reason: defaultReason.trim() });
      toast.success(t('Saved.'));
      await res.reload();
    } catch (e) {
      setDefaultError(serverMessage(e, t));
    } finally {
      setDefaultBusy(false);
    }
  };

  // ── Checks ──
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const checks = useMemo(() => orderChecks(data?.checks ?? []), [data]);
  const focusDefault = () => document.querySelector<HTMLElement>('[data-testid="cc-ready-default-cc"]')?.focus();

  // ── Turn on / off dialogs ──
  const [dialog, setDialog] = useState<'on' | 'off' | null>(null);
  const [preview, setPreview] = useState<CostAccountingReadiness | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState('');
  const [ack, setAck] = useState(false);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [dialogError, setDialogError] = useState('');

  const loadPreview = async () => {
    if (!companyId) return;
    setPreviewLoading(true);
    setPreviewError('');
    try {
      setPreview(await financeGlApi.costAccountingReadiness(companyId));
    } catch (e) {
      setPreview(null);
      setPreviewError(serverMessage(e, t));
    } finally {
      setPreviewLoading(false);
    }
  };
  const openDialog = (kind: 'on' | 'off') => {
    setDialog(kind);
    setAck(false);
    setReason('');
    setDialogError('');
    setPreview(null);
    if (kind === 'on') void loadPreview();
  };
  const gate = preview ? turnOnGate(preview, ack, reason) : null;
  const confirm = async () => {
    if (!companyId) return;
    setBusy(true);
    setDialogError('');
    try {
      if (dialog === 'on') {
        await financeGlApi.setCostAccounting(companyId, { enabled: true, reason: reason.trim(), ...(gate?.needsAck ? { acknowledgeBlockers: ack } : {}) });
        toast.success(t('Cost accounting turned on.'));
      } else {
        await financeGlApi.setCostAccounting(companyId, { enabled: false, reason: reason.trim() });
        toast.success(t('Cost accounting turned off.'));
      }
      setDialog(null);
      await res.reload();
    } catch (e) {
      setDialogError(serverMessage(e, t));
    } finally {
      setBusy(false);
    }
  };

  if (!companyId) {
    return <div className="surface p-6 text-center text-sm text-slate-500">{t('No companies are available in your scope.')}</div>;
  }

  return (
    <div className="space-y-4">
      <section className="surface p-4" aria-labelledby="cc-ready-heading">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <h3 id="cc-ready-heading" className="text-sm font-semibold text-slate-900 dark:text-white">
              {t('Cost accounting for {company}', { company: companyName })}
            </h3>
            <p className="mt-0.5 max-w-xl text-xs text-slate-500 dark:text-slate-400">
              {t('When on, payroll journals are split by cost centre when a run is locked. When off, nothing changes.')}
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            {scope === null ? (
              <select
                value={companyId}
                onChange={(e) => setPicked(e.target.value)}
                aria-label={t('Company')}
                data-testid="cc-ready-company"
                className="select h-8 min-w-[200px] text-sm"
              >
                {companies.map((c) => (
                  <option key={c.id} value={c.id}>{locale === 'ar' && c.legalNameAr ? c.legalNameAr : c.legalNameEn}</option>
                ))}
              </select>
            ) : (
              <span data-testid="cc-ready-company" className="sr-only">{companyName}</span>
            )}
            <div className="flex items-center gap-2">
              <span data-testid="cc-ready-state" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{enabled ? t('Cost accounting is on') : t('Cost accounting is off')}</span>
              <button
                type="button"
                role="switch"
                aria-checked={enabled}
                aria-label={t('Cost accounting')}
                aria-describedby={awaiting ? 'cc-ready-locked-text' : !canManage ? 'cc-ready-readonly-text' : undefined}
                disabled={!canChange}
                onClick={() => openDialog(enabled ? 'off' : 'on')}
                data-testid="cc-ready-switch"
                className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full border border-transparent transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire disabled:cursor-not-allowed disabled:opacity-50 ${enabled ? 'bg-sapphire' : 'bg-slate-300 dark:bg-white/20'}`}
              >
                <span aria-hidden className={`inline-block h-5 w-5 rounded-full bg-white shadow transition-[margin] ${enabled ? 'ms-5' : 'ms-0.5'}`} />
              </button>
            </div>
          </div>
        </div>

        {awaiting && (
          <div role="alert" data-testid="cc-ready-locked-banner" className="mt-3 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-xs text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
            <p id="cc-ready-locked-text">{m7}</p>
          </div>
        )}
        {!canManage && (
          <p id="cc-ready-readonly-text" data-testid="cc-ready-readonly-note" role="note" className="mt-3 flex items-start gap-1.5 rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-700 dark:bg-white/[0.04] dark:text-slate-300">
            <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
            {t('You can view readiness but not change it. Changing needs the Manage GL permission.')}
          </p>
        )}

        <div className="mt-4 max-w-md">
          <Field label={t('Default cost centre')} hint={t('Used when an employee has no cost centre at any level, and for rounding differences.')}>
            <select
              value={defaultId}
              dir={textDir((() => { const c = defaultOptions.find((x) => x.id === defaultId); return c ? `${c.code} — ${c.name}` : t('None'); })())}
              onChange={(e) => setDefaultId(e.target.value)}
              disabled={!canChange}
              data-testid="cc-ready-default-cc"
              className="select w-full disabled:opacity-60"
            >
              <option value="">{t('None')}</option>
              {defaultOptions.map((c) => (
                <option key={c.id} value={c.id}>{`${c.code} — ${c.name}`}</option>
              ))}
            </select>
          </Field>
          {defaultChanged && canChange && (
            <div className="mt-2 space-y-2">
              <FieldError error={defaultError} />
              <Field label={t('Reason for this change')} required hint={t('Recorded in the audit log.')}>
                <input value={defaultReason} onChange={(e) => setDefaultReason(e.target.value)} data-testid="cc-ready-default-reason" className="input w-full" maxLength={500} />
              </Field>
              <button type="button" onClick={() => void saveDefault()} disabled={defaultBusy || !defaultReason.trim()} data-testid="cc-ready-default-save" className="btn-primary h-8 px-3 text-xs disabled:opacity-60">
                {defaultBusy ? t('Saving…') : t('Save default')}
              </button>
            </div>
          )}
        </div>
      </section>

      <section className="surface p-4" aria-labelledby="cc-ready-checks-heading">
        <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
          <h3 id="cc-ready-checks-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Readiness checks')}</h3>
          <button type="button" onClick={() => void res.reload()} disabled={res.loading} data-testid="cc-ready-rerun" className="btn-secondary h-8 px-3 text-xs disabled:opacity-60">
            <RotateCcw className="h-3.5 w-3.5" aria-hidden /> {t('Re-run checks')}
          </button>
        </div>

        {res.error ? (
          <LoadError message={res.error} onRetry={() => void res.reload()} />
        ) : res.loading && !data ? (
          <Spinner />
        ) : (
          <>
            {readinessAllClear(checks) && (
              <p role="status" data-testid="cc-ready-allclear" className="mb-3 flex items-center gap-1.5 rounded-lg bg-emerald-50 px-3 py-2 text-sm font-medium text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300">
                <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden /> {t('Ready. Nothing blocks the next Lock.')}
              </p>
            )}
            <ul className="divide-y divide-slate-100 rounded-lg border border-slate-200 dark:divide-white/5 dark:border-white/10">
              {checks.map((c) => {
                const expanded = !!open[c.key];
                const fix = c.count > 0 && c.severity !== 'ok' ? checkFixTarget(c.key) : null;
                const hiddenCount = Math.max(0, c.count - c.sample.length);
                return (
                  <li key={c.key} className="px-3 py-2">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <button
                        type="button"
                        aria-expanded={expanded}
                        aria-controls={`cc-ready-list-${c.key}`}
                        onClick={() => setOpen((prev) => ({ ...prev, [c.key]: !prev[c.key] }))}
                        data-testid={`cc-ready-check-${c.key}`}
                        className="flex min-h-8 min-w-0 flex-1 items-center gap-2 rounded text-start focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire"
                      >
                        {expanded ? <ChevronDown className="h-4 w-4 shrink-0" aria-hidden /> : <ChevronRight className="h-4 w-4 shrink-0 rtl:rotate-180" aria-hidden />}
                        <SeverityMark severity={c.severity} />
                        <span className="min-w-0 flex-1 text-sm text-slate-800 dark:text-slate-100">{checkLabel(t, c.key)}</span>
                        <bdi dir="ltr" data-testid={`cc-ready-check-count-${c.key}`} className="text-sm font-semibold tabular-nums text-slate-900 dark:text-white">{c.count}</bdi>
                      </button>
                      {fix?.kind === 'link' && (
                        <Link href={fix.href} data-testid={`cc-ready-check-fix-${c.key}`} className="btn-secondary h-8 px-3 text-xs">{fixLabel(t, c.key)}</Link>
                      )}
                      {fix?.kind === 'tab' && (
                        <button type="button" onClick={() => onOpenTab(fix.tab)} data-testid={`cc-ready-check-fix-${c.key}`} className="btn-secondary h-8 px-3 text-xs">{fixLabel(t, c.key)}</button>
                      )}
                      {fix?.kind === 'default' && (
                        <button type="button" onClick={focusDefault} data-testid={`cc-ready-check-fix-${c.key}`} className="btn-secondary h-8 px-3 text-xs">{fixLabel(t, c.key)}</button>
                      )}
                    </div>
                    {expanded && (
                      <div id={`cc-ready-list-${c.key}`} data-testid={`cc-ready-check-list-${c.key}`} className="mt-2 ps-6 text-xs text-slate-700 dark:text-slate-300">
                        {c.count === 0 ? (
                          <p>{t('Nothing to fix.')}</p>
                        ) : c.sample.length === 0 ? (
                          <p>{t('Only the count is shown for this check.')}</p>
                        ) : (
                          <ul className="space-y-1">
                            {c.sample.map((s) => {
                              const href = sampleHref(c.key, s);
                              const label = <bdi dir="ltr" className="font-mono">{s.code}</bdi>;
                              const detail = c.key === 'employees-unresolved' ? blockerReason(s) : null;
                              const fixes = c.key === 'employees-unresolved' ? blockerFixLinks(s) : [];
                              return (
                                <li key={`${s.kind ?? ''}-${s.id}`} data-testid={`cc-ready-sample-${c.key}-${s.id}`} className="min-h-6">
                                  {href ? <Link href={href} className="font-medium text-blue-700 underline underline-offset-2 dark:text-cyanAccent">{label}</Link> : label}
                                  {detail && <span className="ms-2 text-slate-600 dark:text-slate-300">{blockerReasonText(t, detail)}</span>}
                                  {fixes.length > 0 && (
                                    <span className="ms-2 inline-flex flex-wrap items-center gap-x-3 gap-y-1">
                                      {fixes.map((f) => (
                                        <Link key={f.kind} href={f.href} data-testid={`cc-ready-sample-fix-${f.kind}`} className="inline-flex min-h-6 items-center font-medium text-blue-700 underline underline-offset-2 dark:text-cyanAccent">
                                          {f.kind === 'employee' ? t('Employee') : f.kind === 'department' ? t('Department') : f.kind === 'position' ? t('Position') : t('Parent department')}
                                        </Link>
                                      ))}
                                    </span>
                                  )}
                                </li>
                              );
                            })}
                          </ul>
                        )}
                        {hiddenCount > 0 && c.sample.length > 0 && <p className="mt-1 text-slate-500">{t('…and {count} more.', { count: hiddenCount })}</p>}
                      </div>
                    )}
                  </li>
                );
              })}
            </ul>
          </>
        )}
      </section>

      {/* Turn on: what the next Lock would hit, then the acknowledgement and a reason */}
      <Modal
        isOpen={dialog === 'on'}
        title={t('Turn on cost accounting for {company}?', { company: companyName })}
        onClose={() => setDialog(null)}
        footer={
          <>
            <button type="button" onClick={() => setDialog(null)} className="btn-secondary">{t('Cancel')}</button>
            {gate?.needsAck && (
              <button type="button" onClick={() => setDialog(null)} data-testid="cc-on-dialog-fix-first" className="inline-flex h-9 items-center gap-2 rounded-lg bg-blue-700 px-4 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-blue-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire">{t('Fix first')}</button>
            )}
            <button
              type="button"
              onClick={() => void confirm()}
              disabled={busy || !gate || !gate.canConfirm}
              data-testid="cc-on-dialog-confirm"
              className={`${gate?.needsAck ? 'btn-secondary' : 'btn-primary'} disabled:opacity-60`}
            >
              {busy ? t('Saving…') : gate?.needsAck ? t('Turn on anyway') : t('Turn on')}
            </button>
          </>
        }
      >
        <div data-testid="cc-on-dialog" className="space-y-3 text-sm">
          <FieldError error={dialogError} />
          {previewLoading && <div data-testid="cc-on-dialog-loading"><Spinner /></div>}
          {previewError && !previewLoading && (
            <div data-testid="cc-on-dialog-error">
              <p className="mb-2 text-sm text-slate-700 dark:text-slate-200">{t('The preview of what would block Lock could not be loaded.')}</p>
              <LoadError message={previewError} onRetry={() => void loadPreview()} />
            </div>
          )}
          {gate && preview && !previewLoading && (
            <div role="status" className="space-y-3">
              {gate.needsAck ? (
                <>
                  <p>{t('If you turn this on now, the next Lock would be blocked by the problems below.')}</p>
                  <ul className="list-disc space-y-1 ps-5">
                    {gate.hard.map((c) => (
                      <li key={c.key} data-testid={`cc-on-dialog-blocker-${c.key}`}>
                        <bdi dir="ltr" className="font-semibold tabular-nums">{c.count}</bdi> {checkLabel(t, c.key)}
                      </li>
                    ))}
                  </ul>
                  {gate.exposure > 0 && (
                    <p className="flex items-start gap-1.5 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:bg-amber-500/10 dark:text-amber-300">
                      <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
                      {t('{count, plural, one {# cost centre holds fewer than {threshold} employees, so its totals could point at one person.} other {# cost centres hold fewer than {threshold} employees, so their totals could point at one person.}}', { count: gate.exposure, threshold: preview.smallCellExposure.threshold })}
                    </p>
                  )}
                  <label className="flex items-start gap-2">
                    <input type="checkbox" checked={ack} onChange={(e) => setAck(e.target.checked)} data-testid="cc-on-dialog-ack" className="mt-1 h-4 w-4" />
                    <span>{t('I understand payroll Lock will be blocked until these are fixed.')}</span>
                  </label>
                </>
              ) : (
                <p>{t('Nothing blocks the next Lock.')}</p>
              )}
            </div>
          )}
          <Field label={t('Reason for this change')} required hint={t('Recorded in the audit log.')}>
            <input value={reason} onChange={(e) => setReason(e.target.value)} data-testid="cc-on-dialog-reason" className="input w-full" maxLength={500} />
          </Field>
        </div>
      </Modal>

      {/* Turn off */}
      <Modal
        isOpen={dialog === 'off'}
        title={t('Turn off cost accounting for {company}?', { company: companyName })}
        onClose={() => setDialog(null)}
        footer={
          <>
            <button type="button" onClick={() => setDialog(null)} className="btn-secondary">{t('Cancel')}</button>
            <button type="button" onClick={() => void confirm()} disabled={busy || !reason.trim()} data-testid="cc-off-dialog-confirm" className="btn-primary disabled:opacity-60">
              {busy ? t('Saving…') : t('Turn off cost accounting')}
            </button>
          </>
        }
      >
        <div data-testid="cc-off-dialog" className="space-y-3 text-sm">
          <FieldError error={dialogError} />
          <p>{t('Payroll stops splitting by cost centre. Lines already posted keep their cost centres.')}</p>
          <Field label={t('Reason for this change')} required hint={t('Recorded in the audit log.')}>
            <input value={reason} onChange={(e) => setReason(e.target.value)} data-testid="cc-off-dialog-reason" className="input w-full" maxLength={500} />
          </Field>
        </div>
      </Modal>
    </div>
  );
}
