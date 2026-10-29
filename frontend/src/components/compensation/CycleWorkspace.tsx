'use client';

import { pickable } from '../../lib/pickable';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { CheckCircle2, Plus, Rocket, Undo2 } from 'lucide-react';
import {
  compensationApi, compRefusal, type Blocker, type Cycle, type CycleSummary, type EligibilityInput, type FxRate, type Guideline, type PoolType, type ProrationRule,
} from '../../api/compensation';
import { departmentsApi, gradesApi, type DepartmentDto, type GradeDto } from '../../api/organization';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { Modal } from '../Modal';
import { Badge, Field, FieldError, PanelState, serverMessage, theadClass, thClass, todayIso, useConfirm } from '../gl/glUi';
import { BudgetChart } from './BudgetChart';
import { BlockerList, changeStatusLabel, changeStatusTone, cycleStatusLabel, cycleStatusTone, worksheetStatusLabel, worksheetStatusTone } from './compUi';
import { WorksheetEditor } from './WorksheetEditor';

const firstOfNextMonth = () => {
  const d = new Date();
  const n = new Date(d.getFullYear(), d.getMonth() + 1, 1);
  return `${n.getFullYear()}-${String(n.getMonth() + 1).padStart(2, '0')}-01`;
};

/**
 * HR's merit cycle workspace: the cycle list, and for one cycle its single next action, what blocks it, budgets and
 * utilisation, the guideline matrix, FX rates, eligibility, every manager's worksheet, and — once applied — where each
 * award stands on its way to pay of record. Every figure is the server's; each total states what it counts.
 */
export function CycleWorkspace() {
  const t = useT();
  const f = useFormat();
  const { hasPermission } = useAuth();
  const canManage = hasPermission('compensation.manage');
  const canApprove = hasPermission('compensation.approve');
  const [cycles, setCycles] = useState<CycleSummary[] | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [cycle, setCycle] = useState<Cycle | null>(null);
  const [cycleError, setCycleError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [openSheet, setOpenSheet] = useState<string | null>(null);

  const loadList = useCallback(async () => {
    setListError(null);
    try {
      const list = await compensationApi.cycles();
      setCycles(list);
      setSelected((s) => s ?? list.find((c) => c.status !== 'Cancelled')?.id ?? list[0]?.id ?? null);
    } catch (err) { setListError(serverMessage(err, t)); }
  }, [t]);
  const loadCycle = useCallback(async () => {
    if (!selected) { setCycle(null); return; }
    setCycleError(null);
    try { setCycle(await compensationApi.cycle(selected)); } catch (err) { setCycleError(serverMessage(err, t)); }
  }, [selected, t]);
  useEffect(() => { void loadList(); }, [loadList]);
  useEffect(() => { void loadCycle(); }, [loadCycle]);

  const refresh = (c: Cycle) => { setCycle(c); void loadList(); };

  return (
    <div className="grid gap-4 lg:grid-cols-[16rem_1fr]">
      <aside className="surface h-fit space-y-2 p-3" aria-label={t('Merit cycles')}>
        <div className="flex items-center justify-between gap-2">
          <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Merit cycles')}</h3>
          {canManage && (
            <button type="button" className="btn-secondary h-8 px-2 text-xs" onClick={() => setCreating(true)}>
              <Plus className="h-3.5 w-3.5" aria-hidden /> {t('New cycle')}
            </button>
          )}
        </div>
        <PanelState loading={cycles == null} error={listError} onRetry={loadList} empty={(cycles ?? []).length === 0}
          emptyLabel={canManage ? t('No merit cycle yet. Create one to start planning.') : t('No merit cycle yet.')}>
          <ul className="space-y-1">
            {(cycles ?? []).map((c) => (
              <li key={c.id}>
                <button type="button" onClick={() => setSelected(c.id)} aria-current={selected === c.id}
                  className={`w-full rounded-lg px-2 py-2 text-start text-sm ${selected === c.id ? 'bg-sapphire/10 text-sapphire dark:text-cyanAccent' : 'hover:bg-slate-50 dark:hover:bg-white/5'}`}>
                  <span className="block font-medium">{c.name}</span>
                  <span className="mt-0.5 flex flex-wrap items-center gap-1 text-caption text-slate-500 dark:text-slate-400">
                    <Badge tone={cycleStatusTone(c.status)}>{cycleStatusLabel(t, c.status)}</Badge>
                    {t('Effective {date}', { date: f.date(c.effectiveDate) })}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </PanelState>
      </aside>

      <div className="min-w-0">
        <PanelState loading={!!selected && !cycle && !cycleError} error={cycleError} onRetry={loadCycle} empty={!selected}
          emptyLabel={t('Choose a cycle on the left.')}>
          {cycle && <CycleDetail cycle={cycle} canManage={canManage} canApprove={canApprove} onChange={refresh} onOpenSheet={setOpenSheet} reload={loadCycle} />}
        </PanelState>
      </div>

      {creating && <CreateCycleModal onClose={() => setCreating(false)} onCreated={(c) => { setCreating(false); setSelected(c.id); refresh(c); }} />}
      <Modal isOpen={!!openSheet} title={t('Worksheet')} onClose={() => { setOpenSheet(null); void loadCycle(); }} size="xl">
        {openSheet && <WorksheetEditor worksheetId={openSheet} onChanged={loadCycle} />}
      </Modal>
    </div>
  );
}

// ───────────────────────────── one cycle ─────────────────────────────

function CycleDetail({ cycle, canManage, canApprove, onChange, onOpenSheet, reload }: {
  cycle: Cycle; canManage: boolean; canApprove: boolean; onChange: (c: Cycle) => void; onOpenSheet: (id: string) => void; reload: () => void;
}) {
  const t = useT();
  const f = useFormat();
  const { confirm, dialog } = useConfirm();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [blockers, setBlockers] = useState<Blocker[]>([]);
  const [cancelOpen, setCancelOpen] = useState(false);
  const [reason, setReason] = useState('');
  const can = (a: string) => cycle.allowedActions.includes(a);
  const [reopenOpen, setReopenOpen] = useState(false);
  const [reopenReason, setReopenReason] = useState('');
  const [assignTo, setAssignTo] = useState<Record<number, string>>({});
  const planning = cycle.status === 'Planning';
  const assignable = cycle.worksheets.filter((w) => w.isPrimary && (w.status === 'Draft' || w.status === 'Returned'));
  const retroText = (c: Cycle) => t('The effective date is {months, plural, one {# month} other {# months}} in the past. Payroll for {periods} already ran at the old pay, so these awards owe about {cost} in arrears ({count, plural, one {# month} other {# months}} × {delta}).', {
    months: c.retro?.monthsInPast ?? 0, periods: (c.retro?.paidPeriods ?? []).join(', ') || '—', cost: f.money(c.retro?.retroCostCycle ?? 0, c.currency),
    count: c.retro?.paidPeriods.length ?? 0, delta: f.money(c.retro?.monthlyDeltaCycle ?? 0, c.currency),
  });
  const closeSheet = async (id: string, name: string) => {
    const ok = await confirm({ title: t('Close the worksheet of {name}?', { name }), message: t('The worksheet is cancelled and its people show as having no worksheet, so you can assign them to another one.'), confirmLabel: t('Close worksheet') });
    if (ok) await act(() => compensationApi.closeWorksheet(id, t('Closed by HR from the cycle workspace')));
  };

  const act = async (work: () => Promise<Cycle>, retryAcknowledged?: () => Promise<Cycle>) => {
    setBusy(true); setError(''); setBlockers([]);
    try { onChange(await work()); } catch (err) {
      const r = compRefusal(err);
      // A payroll month ran between approval and apply: an approver may acknowledge the arrears and schedule anyway.
      if (r?.error === 'retro_acknowledgement_required' && retryAcknowledged && canApprove) {
        setBusy(false);
        const ok = await confirm({ title: t('Schedule with a retro cost?'), message: r.message, confirmLabel: t('I acknowledge the retro cost — schedule') });
        if (ok) await act(retryAcknowledged);
        return;
      }
      setBlockers(r?.blockers ?? []);
      setError(r?.message ?? serverMessage(err, t));
      if (r?.error === 'conflict') reload();
    } finally { setBusy(false); }
  };

  const next = (() => {
    if (cycle.status === 'Draft' && canManage) return { label: t('Open planning'), hint: t('Opens a worksheet for every manager with eligible direct reports.'), run: () => compensationApi.transition(cycle.id, 'open-planning', cycle.version) };
    if (cycle.status === 'Planning' && canManage) return { label: t('Send the cycle to review'), hint: t('Every manager’s worksheet must be approved first.'), run: () => compensationApi.transition(cycle.id, 'start-review', cycle.version) };
    if (cycle.status === 'Review' && canApprove) return { label: t('Approve the cycle'), hint: t('A different person from the one who sent it to review must approve it.'), run: () => compensationApi.transition(cycle.id, 'approve', cycle.version, undefined, !!cycle.retro?.acknowledgementRequired) };
    if (cycle.status === 'Approved' && canManage) return { label: t('Schedule the awards'), hint: t('Each award becomes an approved salary change that takes effect on {date}.', { date: f.date(cycle.effectiveDate) }), run: () => compensationApi.apply(cycle.id) };
    if (cycle.status === 'Applied' && canManage && (cycle.apply?.blocked ?? 0) > 0) return { label: t('Schedule the remaining awards'), hint: t('Picks up awards that were blocked and have since been resolved. Nothing is scheduled twice.'), run: () => compensationApi.apply(cycle.id) };
    return null;
  })();

  const runNext = async () => {
    if (!next) return;
    if (cycle.status === 'Review' && cycle.retro?.acknowledgementRequired) {
      const ok = await confirm({ title: t('Approve with a retro cost?'), message: retroText(cycle), confirmLabel: t('I acknowledge the retro cost — approve') });
      if (!ok) return;
    }
    if (cycle.status === 'Approved') {
      const ok = await confirm({ title: t('Schedule the awards?'), message: t('Each approved award becomes a salary change that payroll applies on {date}. You can still cancel before that date.', { date: f.date(cycle.effectiveDate) }), confirmLabel: t('Schedule the awards') });
      if (!ok) return;
    }
    const applying = cycle.status === 'Approved' || cycle.status === 'Applied';
    await act(next.run, applying ? () => compensationApi.apply(cycle.id, true) : undefined);
  };

  return (
    <section className="space-y-4" aria-labelledby="cycle-heading" data-testid="cycle-detail">
      {dialog}
      <header className="surface flex flex-wrap items-start justify-between gap-3 p-4">
        <div>
          <h2 id="cycle-heading" className="text-lg font-semibold text-slate-900 dark:text-white">{cycle.name}</h2>
          <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
            {t('{company} · performance period {start} – {end} · effective {date} · planned in {currency}', {
              company: cycle.companyName, start: f.date(cycle.performancePeriodStart), end: f.date(cycle.performancePeriodEnd), date: f.date(cycle.effectiveDate), currency: cycle.currency,
            })}
          </p>
          <div className="mt-2"><Badge tone={cycleStatusTone(cycle.status)}>{cycleStatusLabel(t, cycle.status)}</Badge></div>
          {cycle.cancelReason && <p className="mt-2 text-xs text-slate-600 dark:text-slate-300">{t('Cancelled: {reason}', { reason: cycle.cancelReason })}</p>}
        </div>
        <div className="flex max-w-sm flex-col items-end gap-2">
          {next && (
            <>
              <button type="button" className="btn-primary" disabled={busy} onClick={runNext} data-testid="cycle-next-action">
                <Rocket className="h-4 w-4" aria-hidden /> {next.label}
              </button>
              <p className="text-end text-caption text-slate-500 dark:text-slate-400">{next.hint}</p>
            </>
          )}
          <div className="flex flex-wrap justify-end gap-2">
            {can('reopen') && canManage && (
              <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={busy} onClick={() => setReopenOpen(true)} data-testid="cycle-reopen">
                <Undo2 className="h-3.5 w-3.5" aria-hidden /> {cycle.status === 'Review' ? t('Back to planning') : t('Reopen planning')}
              </button>
            )}
            {can('cancel') && canManage && (
              <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={busy} onClick={() => setCancelOpen(true)}>{t('Cancel cycle')}</button>
            )}
          </div>
        </div>
      </header>

      <FieldError error={error} />
      {cycle.retro && (
        <div role="status" className="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-400/30 dark:bg-amber-400/10 dark:text-amber-200" data-testid="cycle-retro">
          <p className="font-medium">{t('Retroactive effective date')}</p>
          <p className="mt-0.5 text-xs">{retroText(cycle)}</p>
          {cycle.retro.acknowledgementRequired && <p className="mt-1 text-xs">{t('The approver must acknowledge this cost to approve the cycle.')}</p>}
        </div>
      )}
      <BlockerList blockers={blockers.length ? blockers : cycle.blockers} t={t} title={cycle.status === 'Draft' ? t('Planning cannot open until these are done') : undefined} />

      <Totals cycle={cycle} />

      <div className="surface space-y-3 p-4">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Budgets and utilisation')}</h3>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('Pool: a percentage of the department’s eligible monthly basic payroll in {currency}, or a fixed monthly amount. Used: the prorated increases on each manager’s submitted scenario.', { currency: cycle.currency })}</p>
        <BudgetChart budgets={cycle.budgets} currency={cycle.currency} />
        <BudgetTable cycle={cycle} editable={canManage && can('budgets')} onSaved={onChange} />
      </div>

      <GuidelineMatrix cycle={cycle} editable={canManage && can('guidelines')} onSaved={onChange} />
      <FxTable cycle={cycle} editable={canManage && can('fx')} onSaved={onChange} />
      <EligibilitySummary cycle={cycle} onSaved={onChange} editable={canManage && can('edit')} />

      <div className="surface space-y-2 p-4">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Manager worksheets')}</h3>
        {cycle.worksheets.length === 0 ? (
          <p className="text-xs text-slate-500 dark:text-slate-400">{t('Worksheets open when planning opens.')}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[640px] text-sm" data-testid="cycle-worksheets">
              <thead className={theadClass}><tr>
                <th className={thClass}>{t('Manager')}</th><th className={thClass}>{t('Scenario')}</th><th className={thClass}>{t('Status')}</th>
                <th className={thClass}>{t('People')}</th><th className={thClass}>{t('Proposed')}</th><th className={thClass}><span className="sr-only">{t('Open')}</span></th>
              </tr></thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {cycle.worksheets.map((w) => (
                  <tr key={w.id} data-manager={w.managerName}>
                    <td className="px-3 py-2 font-medium">{w.managerName}</td>
                    <td className="px-3 py-2">{w.isPrimary ? t('{name} (submitted scenario)', { name: w.scenarioName }) : w.scenarioName}</td>
                    <td className="px-3 py-2">
                      <Badge tone={worksheetStatusTone(w.status)}>{worksheetStatusLabel(t, w.status)}</Badge>
                      {w.overspend && <span className="ms-1"><Badge tone="rose">{t('Over budget')}</Badge></span>}
                      {w.decisionComments && <p className="mt-1 max-w-[16rem] text-caption text-slate-500 dark:text-slate-400">{w.decisionComments}</p>}
                    </td>
                    <td className="px-3 py-2 tabular-nums">{f.integer(w.lineCount)}</td>
                    <td className="px-3 py-2 tabular-nums">{f.money(w.proposedTotal, cycle.currency)}</td>
                    <td className="px-3 py-2 text-end">
                      <div className="flex flex-wrap justify-end gap-2">
                        <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => onOpenSheet(w.id)}>{t('Open worksheet of {name}', { name: w.managerName })}</button>
                        {canManage && planning && (w.status === 'Draft' || w.status === 'Returned') && (
                          <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={busy} onClick={() => closeSheet(w.id, w.managerName)}>{t('Close worksheet of {name}', { name: w.managerName })}</button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {cycle.uncovered.length > 0 && (
          <details className="text-xs text-amber-800 dark:text-amber-300" open>
            <summary className="cursor-pointer font-medium">{t('{count, plural, one {# eligible employee has no worksheet} other {# eligible employees have no worksheet}}', { count: cycle.uncovered.length })}</summary>
            <ul className="mt-1 space-y-2 ps-1" data-testid="cycle-uncovered">
              {cycle.uncovered.map((u) => (
                <li key={u.employeeId} data-employee={u.name}>
                  <span>{`${u.name}: ${u.reason}`}</span>
                  {canManage && planning && (
                    <div className="mt-1 flex flex-wrap items-center gap-2">
                      {u.ownerCanBeOpened && u.ownerEmployeeId != null && (
                        <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={busy}
                          onClick={() => act(async () => { await compensationApi.openWorksheet(cycle.id, u.ownerEmployeeId!); return compensationApi.cycle(cycle.id); })}>
                          {t('Open a worksheet for {name}', { name: u.ownerName ?? '' })}
                        </button>
                      )}
                      {assignable.length > 0 && (
                        <>
                          <label className="sr-only" htmlFor={`assign-${u.employeeId}`}>{t('Assign {name} to a worksheet', { name: u.name })}</label>
                          <select id={`assign-${u.employeeId}`} className="input h-8 w-auto py-0 text-xs" value={assignTo[u.employeeId] ?? ''}
                            onChange={(e) => setAssignTo((m) => ({ ...m, [u.employeeId]: e.target.value }))}>
                            <option value="">{t('Choose a worksheet owner')}</option>
                            {assignable.map((w) => <option key={w.id} value={w.id}>{w.managerName}</option>)}
                          </select>
                          <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={busy || !assignTo[u.employeeId]}
                            onClick={() => act(async () => { await compensationApi.assign(assignTo[u.employeeId], u.employeeId); return compensationApi.cycle(cycle.id); })}>
                            {t('Assign {name}', { name: u.name })}
                          </button>
                        </>
                      )}
                    </div>
                  )}
                </li>
              ))}
            </ul>
          </details>
        )}
        {cycle.ineligible.length > 0 && (
          <details className="text-xs text-slate-600 dark:text-slate-300">
            <summary className="cursor-pointer font-medium">{t('{count, plural, one {# employee is not eligible} other {# employees are not eligible}}', { count: cycle.ineligible.length })}</summary>
            <ul className="mt-1 list-disc ps-5">{cycle.ineligible.map((u) => <li key={u.employeeId}>{`${u.name}: ${u.reason}`}</li>)}</ul>
          </details>
        )}
      </div>

      {cycle.apply && <ApplyResults cycle={cycle} canManage={canManage} canApprove={canApprove} onDone={reload} />}

      <Modal isOpen={reopenOpen} title={cycle.status === 'Review' ? t('Send the cycle back to planning?') : t('Reopen planning?')} onClose={() => setReopenOpen(false)} size="sm"
        footer={<>
          <button type="button" className="btn-secondary" onClick={() => setReopenOpen(false)}>{t('Keep it as it is')}</button>
          <button type="button" className="btn-primary" disabled={reopenReason.trim().length < 3 || busy}
            onClick={async () => { setReopenOpen(false); await act(() => compensationApi.transition(cycle.id, 'reopen', cycle.version, reopenReason.trim())); setReopenReason(''); }}>
            {t('Reopen planning')}
          </button>
        </>}>
        <p className="mb-2 text-sm text-slate-600 dark:text-slate-300">{t('Every submitted or approved worksheet goes back to its manager as Returned, and pending approvals close. Budgets and exchange rates can then be changed. Each worksheet must be submitted and approved again. This is recorded in the audit log.')}</p>
        <Field label={t('Reason')} required><textarea className="input min-h-[64px] w-full" value={reopenReason} maxLength={1000} onChange={(e) => setReopenReason(e.target.value)} /></Field>
      </Modal>

      <Modal isOpen={cancelOpen} title={t('Cancel this cycle?')} onClose={() => setCancelOpen(false)} size="sm"
        footer={<>
          <button type="button" className="btn-secondary" onClick={() => setCancelOpen(false)}>{t('Keep the cycle')}</button>
          <button type="button" className="btn-primary" disabled={reason.trim().length < 3 || busy}
            onClick={async () => { setCancelOpen(false); await act(() => compensationApi.transition(cycle.id, 'cancel', cycle.version, reason.trim())); setReason(''); }}>
            {t('Cancel cycle')}
          </button>
        </>}>
        <p className="mb-2 text-sm text-slate-600 dark:text-slate-300">{t('Pending approvals are closed and any award that has not taken effect is withdrawn. Awards already in pay of record are not undone; reverse them one by one.')}</p>
        <Field label={t('Reason')} required><textarea className="input min-h-[64px] w-full" value={reason} maxLength={1000} onChange={(e) => setReason(e.target.value)} /></Field>
      </Modal>
    </section>
  );
}

function Totals({ cycle }: { cycle: Cycle }) {
  const t = useT();
  const f = useFormat();
  const tiles = [
    { label: t('Eligible people'), value: f.integer(cycle.totals.eligibleCount), definition: t('Employees who meet every eligibility rule of this cycle.') },
    { label: t('Total pool'), value: f.money(cycle.totals.pool, cycle.currency), definition: t('Sum of every department pool, monthly, in {currency}.', { currency: cycle.currency }) },
    { label: t('Proposed'), value: f.money(cycle.totals.proposed, cycle.currency), definition: t('Sum of prorated monthly increases on the submitted scenarios.') },
    { label: t('Pool used'), value: f.percent(cycle.totals.percentUsed, 2), definition: t('Proposed ÷ total pool. Departments are shown separately below.') },
  ];
  return (
    <dl className="grid grid-cols-2 gap-3 md:grid-cols-4" data-testid="cycle-totals">
      {tiles.map((x) => (
        <div key={x.label} className="surface p-3">
          <dt className="text-xs text-slate-500 dark:text-slate-400">{x.label}</dt>
          <dd className="mt-1 text-lg font-semibold tabular-nums text-slate-900 dark:text-white">{x.value}</dd>
          <dd className="mt-1 text-caption text-slate-500 dark:text-slate-400">{x.definition}</dd>
        </div>
      ))}
    </dl>
  );
}

function BudgetTable({ cycle, editable, onSaved }: { cycle: Cycle; editable: boolean; onSaved: (c: Cycle) => void }) {
  const t = useT();
  const f = useFormat();
  const [departments, setDepartments] = useState<DepartmentDto[]>([]);
  const [rows, setRows] = useState<{ departmentId: string; poolType: PoolType; value: string }[]>([]);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  useEffect(() => {
    setRows(cycle.budgets.filter((b) => b.hasBudget).map((b) => ({ departmentId: b.departmentId, poolType: b.poolType ?? 'PercentOfEligibleBasic', value: String((b.poolType === 'FixedAmount' ? b.fixedAmount : b.poolPercent) ?? '') })));
  }, [cycle]);
  useEffect(() => { if (editable) departmentsApi.listAll().then(setDepartments).catch(() => setDepartments([])); }, [editable]);
  const save = async () => {
    setSaving(true); setError('');
    try {
      onSaved(await compensationApi.saveBudgets(cycle.id, rows.filter((r) => r.value.trim() !== '').map((r) => ({
        departmentId: r.departmentId, poolType: r.poolType,
        poolPercent: r.poolType === 'PercentOfEligibleBasic' ? Number(r.value) : null, fixedAmount: r.poolType === 'FixedAmount' ? Number(r.value) : null,
      }))));
    } catch (err) { setError(serverMessage(err, t)); } finally { setSaving(false); }
  };
  const missing = departments.filter((d) => !rows.some((r) => r.departmentId === d.id) && cycle.budgets.some((b) => b.departmentId === d.id && b.eligibleCount > 0));
  return (
    <div className="space-y-2">
      <div className="overflow-x-auto">
        <table className="w-full min-w-[720px] text-sm" data-testid="budget-table">
          <thead className={theadClass}><tr>
            <th className={thClass}>{t('Department')}</th><th className={thClass}>{t('Eligible')}</th><th className={thClass}>{t('Eligible basic')}</th>
            <th className={thClass}>{t('Pool')}</th><th className={thClass}>{t('Proposed')}</th><th className={thClass}>{t('Remaining')}</th><th className={thClass}>{t('Used')}</th>
          </tr></thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/5">
            {cycle.budgets.map((b) => {
              const row = rows.find((r) => r.departmentId === b.departmentId);
              return (
                <tr key={b.departmentId} data-department={b.departmentName}>
                  <td className="px-3 py-2 font-medium">{b.departmentName}{b.overspent && <span className="ms-1"><Badge tone="rose">{t('Over budget')}</Badge></span>}</td>
                  <td className="px-3 py-2 tabular-nums">{f.integer(b.eligibleCount)}</td>
                  <td className="px-3 py-2 tabular-nums">{f.money(b.eligibleBasicCycle, cycle.currency)}</td>
                  <td className="px-3 py-2">
                    {editable && row ? (
                      <div className="flex items-center gap-1">
                        <select className="input h-8 text-xs" value={row.poolType} aria-label={t('Pool type for {dept}', { dept: b.departmentName })}
                          onChange={(e) => setRows((rs) => rs.map((r) => r.departmentId === b.departmentId ? { ...r, poolType: e.target.value as PoolType } : r))}>
                          <option value="PercentOfEligibleBasic">{t('% of eligible basic')}</option>
                          <option value="FixedAmount">{t('Fixed amount')}</option>
                        </select>
                        <input className="input h-8 w-24 text-end text-xs tabular-nums" inputMode="decimal" value={row.value} aria-label={t('Pool value for {dept}', { dept: b.departmentName })}
                          onChange={(e) => setRows((rs) => rs.map((r) => r.departmentId === b.departmentId ? { ...r, value: e.target.value } : r))} />
                      </div>
                    ) : null}
                    <div className="tabular-nums">{b.hasBudget ? f.money(b.pool, cycle.currency) : t('No budget set')}</div>
                    {b.poolType === 'PercentOfEligibleBasic' && b.poolPercent != null && <div className="text-caption text-slate-500 dark:text-slate-400">{t('{pct} of eligible basic', { pct: f.percent(b.poolPercent, 2) })}</div>}
                  </td>
                  <td className="px-3 py-2 tabular-nums">{f.money(b.proposed, cycle.currency)}</td>
                  <td className={`px-3 py-2 tabular-nums ${b.remaining < 0 ? 'text-rose-700 dark:text-rose-300' : ''}`}>{f.money(b.remaining, cycle.currency)}</td>
                  <td className="px-3 py-2 tabular-nums">{f.percent(b.percentUsed, 2)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {editable && (
        <div className="flex flex-wrap items-end gap-2">
          {missing.length > 0 && (
            <label className="flex flex-col gap-1 text-xs">
              <span className="font-medium text-slate-600 dark:text-slate-300">{t('Add a budget for')}</span>
              <select className="input h-8 text-xs" value="" onChange={(e) => e.target.value && setRows((rs) => [...rs, { departmentId: e.target.value, poolType: 'PercentOfEligibleBasic', value: '' }])}>
                <option value="">{t('Choose a department')}</option>
                {missing.map((d) => <option key={d.id} value={d.id}>{d.nameEn}</option>)}
              </select>
            </label>
          )}
          <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={save} disabled={saving}>{saving ? t('Saving…') : t('Save budgets')}</button>
        </div>
      )}
      <FieldError error={error} />
    </div>
  );
}

function GuidelineMatrix({ cycle, editable, onSaved }: { cycle: Cycle; editable: boolean; onSaved: (c: Cycle) => void }) {
  const t = useT();
  const f = useFormat();
  type Row = { rating: string; min: string; max: string; lo: string; target: string; hi: string };
  const toRows = (g: Guideline[]): Row[] => g.map((x) => ({ rating: x.rating, min: String(x.compaRatioMin), max: x.compaRatioMax == null ? '' : String(x.compaRatioMax), lo: String(x.minPercent), target: String(x.targetPercent), hi: String(x.maxPercent) }));
  const [rows, setRows] = useState<Row[]>(toRows(cycle.guidelines));
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  useEffect(() => setRows(toRows(cycle.guidelines)), [cycle]);
  const save = async () => {
    setSaving(true); setError('');
    try {
      onSaved(await compensationApi.saveGuidelines(cycle.id, rows.map((r) => ({
        rating: r.rating.trim(), compaRatioMin: Number(r.min || 0), compaRatioMax: r.max.trim() === '' ? null : Number(r.max),
        minPercent: Number(r.lo), targetPercent: Number(r.target), maxPercent: Number(r.hi),
      }))));
    } catch (err) { setError(serverMessage(err, t)); } finally { setSaving(false); }
  };
  const cell = (i: number, key: keyof Row, label: string, wide = false) => (
    <input className={`input h-8 ${wide ? 'w-32' : 'w-20'} text-xs`} value={rows[i][key]} aria-label={label} inputMode={wide ? 'text' : 'decimal'}
      onChange={(e) => setRows((rs) => rs.map((r, j) => (j === i ? { ...r, [key]: e.target.value } : r)))} />
  );
  return (
    <div className="surface space-y-2 p-4">
      <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Guideline matrix')}</h3>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('Each row: a performance rating (as published in Performance, or Not rated) and a compa-ratio band (from, inclusive, to, exclusive; empty means no upper limit), with the minimum, target and maximum increase. A proposal outside min–max needs a written justification.')}</p>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[640px] text-sm" data-testid="guideline-matrix">
          <thead className={theadClass}><tr>
            <th className={thClass}>{t('Rating')}</th><th className={thClass}>{t('Compa-ratio from')}</th><th className={thClass}>{t('Compa-ratio to')}</th>
            <th className={thClass}>{t('Minimum %')}</th><th className={thClass}>{t('Target %')}</th><th className={thClass}>{t('Maximum %')}</th>
          </tr></thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/5">
            {editable ? rows.map((r, i) => (
              <tr key={i}>
                <td className="px-3 py-1.5">{cell(i, 'rating', t('Rating, row {n}', { n: i + 1 }), true)}</td>
                <td className="px-3 py-1.5">{cell(i, 'min', t('Compa-ratio from, row {n}', { n: i + 1 }))}</td>
                <td className="px-3 py-1.5">{cell(i, 'max', t('Compa-ratio to, row {n}', { n: i + 1 }))}</td>
                <td className="px-3 py-1.5">{cell(i, 'lo', t('Minimum %, row {n}', { n: i + 1 }))}</td>
                <td className="px-3 py-1.5">{cell(i, 'target', t('Target %, row {n}', { n: i + 1 }))}</td>
                <td className="px-3 py-1.5">{cell(i, 'hi', t('Maximum %, row {n}', { n: i + 1 }))}</td>
              </tr>
            )) : cycle.guidelines.map((g) => (
              <tr key={g.id ?? `${g.rating}-${g.compaRatioMin}`}>
                <td className="px-3 py-2 font-medium">{g.rating === 'Unrated' ? t('Not rated') : g.rating}</td>
                <td className="px-3 py-2 tabular-nums">{f.number(g.compaRatioMin, { minimumFractionDigits: 2 })}</td>
                <td className="px-3 py-2 tabular-nums">{g.compaRatioMax == null ? t('No limit') : f.number(g.compaRatioMax, { minimumFractionDigits: 2 })}</td>
                <td className="px-3 py-2 tabular-nums">{f.percent(g.minPercent, 2)}</td>
                <td className="px-3 py-2 tabular-nums">{f.percent(g.targetPercent, 2)}</td>
                <td className="px-3 py-2 tabular-nums">{f.percent(g.maxPercent, 2)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {editable && (
        <div className="flex flex-wrap gap-2">
          <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => setRows((rs) => [...rs, { rating: '', min: '0', max: '', lo: '0', target: '0', hi: '0' }])}>
            <Plus className="h-3.5 w-3.5" aria-hidden /> {t('Add row')}
          </button>
          <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={save} disabled={saving}>{saving ? t('Saving…') : t('Save guideline matrix')}</button>
        </div>
      )}
      <FieldError error={error} />
    </div>
  );
}

function FxTable({ cycle, editable, onSaved }: { cycle: Cycle; editable: boolean; onSaved: (c: Cycle) => void }) {
  const t = useT();
  const f = useFormat();
  const needed = useMemo(() => cycle.payCurrencies.filter((c) => c !== cycle.currency), [cycle]);
  const toRows = () => {
    const known = cycle.fxRates.map((r) => ({ fromCurrency: r.fromCurrency, rate: String(r.rate), source: r.source, rateDate: r.rateDate }));
    return [...known, ...needed.filter((c) => !known.some((k) => k.fromCurrency === c)).map((c) => ({ fromCurrency: c, rate: '', source: '', rateDate: todayIso() }))];
  };
  const [rows, setRows] = useState(toRows);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => setRows(toRows()), [cycle]);
  if (needed.length === 0 && cycle.fxRates.length === 0) return null;
  const save = async () => {
    setSaving(true); setError('');
    try {
      onSaved(await compensationApi.saveFx(cycle.id, rows.filter((r) => r.rate.trim() !== '').map<FxRate>((r) => ({ fromCurrency: r.fromCurrency, rate: Number(r.rate), source: r.source, rateDate: r.rateDate }))));
    } catch (err) { setError(serverMessage(err, t)); } finally { setSaving(false); }
  };
  return (
    <div className="surface space-y-2 p-4">
      <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Exchange rates for this cycle')}</h3>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('Rate = how many {currency} one unit of the other currency is worth. Used to compare budgets and equity only; each award is paid in the employee’s own currency. A missing rate blocks planning — it is never guessed.', { currency: cycle.currency })}</p>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[640px] text-sm" data-testid="fx-table">
          <thead className={theadClass}><tr>
            <th className={thClass}>{t('Currency')}</th><th className={thClass}>{t('Rate')}</th><th className={thClass}>{t('Source')}</th><th className={thClass}>{t('Rate date')}</th><th className={thClass}>{t('Entered by')}</th>
          </tr></thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/5">
            {rows.map((r, i) => {
              const saved = cycle.fxRates.find((x) => x.fromCurrency === r.fromCurrency);
              const upd = (patch: Partial<typeof r>) => setRows((rs) => rs.map((x, j) => (j === i ? { ...x, ...patch } : x)));
              return (
                <tr key={r.fromCurrency}>
                  <td className="px-3 py-2 font-medium">{r.fromCurrency}{!saved && <span className="ms-1"><Badge tone="rose">{t('Missing')}</Badge></span>}</td>
                  <td className="px-3 py-2">{editable ? <input className="input h-8 w-28 text-xs tabular-nums" inputMode="decimal" value={r.rate} aria-label={t('Rate for {currency}', { currency: r.fromCurrency })} onChange={(e) => upd({ rate: e.target.value })} /> : f.number(Number(r.rate), { maximumFractionDigits: 8 })}</td>
                  <td className="px-3 py-2">{editable ? <input className="input h-8 w-48 text-xs" value={r.source} maxLength={200} aria-label={t('Source for {currency}', { currency: r.fromCurrency })} onChange={(e) => upd({ source: e.target.value })} /> : r.source}</td>
                  <td className="px-3 py-2">{editable ? <input type="date" className="input h-8 text-xs" value={r.rateDate} aria-label={t('Rate date for {currency}', { currency: r.fromCurrency })} onChange={(e) => upd({ rateDate: e.target.value })} /> : f.date(r.rateDate)}</td>
                  <td className="px-3 py-2 text-xs text-slate-500 dark:text-slate-400">{saved?.enteredByName ? t('{name} on {date}', { name: saved.enteredByName, date: f.date(saved.enteredAtUtc ?? null) }) : '—'}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {editable && <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={save} disabled={saving}>{saving ? t('Saving…') : t('Save exchange rates')}</button>}
      <FieldError error={error} />
    </div>
  );
}

function EligibilitySummary({ cycle, editable, onSaved }: { cycle: Cycle; editable: boolean; onSaved: (c: Cycle) => void }) {
  const t = useT();
  const f = useFormat();
  const [open, setOpen] = useState(false);
  const [grades, setGrades] = useState<GradeDto[]>([]);
  useEffect(() => { gradesApi.listAll().then(setGrades).catch(() => setGrades([])); }, []);
  const excluded = grades.filter((g) => cycle.excludeGradeIds.includes(g.id)).map((g) => g.name);
  return (
    <div className="surface space-y-2 p-4">
      <div className="flex items-center justify-between gap-2">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Eligibility and proration')}</h3>
        {editable && <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => setOpen(true)}>{t('Edit eligibility')}</button>}
      </div>
      <ul className="space-y-1 text-xs text-slate-600 dark:text-slate-300">
        <li>{cycle.hireDateCutoff ? t('Joined on or before {date}', { date: f.date(cycle.hireDateCutoff) }) : t('No hire-date cutoff')}</li>
        <li>{t('Employee status: {statuses}', { statuses: cycle.eligibleStatuses.join(', ') })}</li>
        <li>{excluded.length ? t('Excluded grades: {grades}', { grades: excluded.join(', ') }) : t('No grade is excluded')}</li>
        <li>{t('At least {days} eligible days in the performance period; anyone on notice or separating is excluded', { days: cycle.minimumEligibleDays })}</li>
        <li>{cycle.prorationRule === 'EligibleDays' ? t('Awards are prorated by eligible days ÷ days in the performance period (4 decimals)') : t('Awards are not prorated')}</li>
        <li>{t('Pay component: basic salary only; allowances are carried unchanged')}</li>
      </ul>
      {open && <CycleFormModal cycle={cycle} onClose={() => setOpen(false)} onSaved={(c) => { setOpen(false); onSaved(c); }} />}
    </div>
  );
}

function ApplyResults({ cycle, canManage, canApprove, onDone }: { cycle: Cycle; canManage: boolean; canApprove: boolean; onDone: () => void }) {
  const t = useT();
  const f = useFormat();
  const a = cycle.apply!;
  const [reverseFor, setReverseFor] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [date, setDate] = useState(firstOfNextMonth());
  const [error, setError] = useState('');
  const [lockedNotice, setLockedNotice] = useState(false);
  const [withdrawFor, setWithdrawFor] = useState<{ lineId: string; name: string } | null>(null);
  const [withdrawReason, setWithdrawReason] = useState('');
  const [withdrawError, setWithdrawError] = useState('');
  const withdraw = async () => {
    if (!withdrawFor) return;
    setWithdrawError('');
    try { await compensationApi.withdraw(withdrawFor.lineId, withdrawReason.trim()); setWithdrawFor(null); setWithdrawReason(''); onDone(); }
    catch (err) { setWithdrawError(compRefusal(err)?.message ?? serverMessage(err, t)); }
  };
  const [acknowledged, setAcknowledged] = useState(false);
  const closeReverse = () => { setReverseFor(null); setLockedNotice(false); setAcknowledged(false); setError(''); };
  const reverse = async () => {
    if (!reverseFor) return;
    setError('');
    try { await compensationApi.reverse(reverseFor, date, reason.trim(), acknowledged); closeReverse(); setReason(''); onDone(); } catch (err) {
      const r = compRefusal(err);
      if (r?.error === 'locked_periods') setLockedNotice(true);
      setError(r?.message ?? serverMessage(err, t));
    }
  };
  return (
    <div className="surface space-y-2 p-4" data-testid="apply-results">
      <h3 className="flex items-center gap-1.5 text-sm font-semibold text-slate-900 dark:text-white"><CheckCircle2 className="h-4 w-4 text-emerald-600" aria-hidden />{t('Awards on their way to pay of record')}</h3>
      <p className="text-xs text-slate-600 dark:text-slate-300">
        {t('{awards} awards: {scheduled} scheduled, {applied} in pay of record, {review} back in Approvals, {blocked} blocked, {cancelled} withdrawn.', {
          awards: a.awards, scheduled: a.scheduled, applied: a.applied, review: a.returnedForReview, blocked: a.blocked, cancelled: a.cancelled,
        })}
      </p>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[640px] text-sm">
          <thead className={theadClass}><tr>
            <th className={thClass}>{t('Employee')}</th><th className={thClass}>{t('New basic')}</th><th className={thClass}>{t('Where it stands')}</th><th className={thClass}><span className="sr-only">{t('Actions')}</span></th>
          </tr></thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/5">
            {a.lines.map((l) => (
              <tr key={l.lineId} data-employee={l.employeeName}>
                <td className="px-3 py-2 font-medium">{l.employeeName}</td>
                <td className="px-3 py-2 tabular-nums">{f.money(l.newBasic, l.payCurrency)}</td>
                <td className="px-3 py-2">
                  <Badge tone={changeStatusTone(l.changeStatus)}>{l.changeRequestId ? changeStatusLabel(t, l.changeStatus) : t('Blocked')}</Badge>
                  {l.blockedReason && !l.changeRequestId && <p className="mt-1 max-w-md text-caption text-rose-700 dark:text-rose-300">{l.blockedReason}</p>}
                  {l.reversalChangeRequestId && <p className="mt-1 text-caption text-slate-600 dark:text-slate-300">{t('Reversal: {status}', { status: changeStatusLabel(t, l.reversalStatus) })}</p>}
                </td>
                <td className="px-3 py-2 text-end">
                  {canManage && (l.changeStatus === 'ApprovedPendingEffectiveDate' || l.changeStatus === 'PendingApproval') && (
                    <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => setWithdrawFor({ lineId: l.lineId, name: l.employeeName })}>{t('Withdraw award of {name}', { name: l.employeeName })}</button>
                  )}
                  {canManage && l.changeStatus === 'ApprovedApplied' && !l.reversalChangeRequestId && (
                    <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => setReverseFor(l.lineId)}>{t('Reverse award of {name}', { name: l.employeeName })}</button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <Modal isOpen={!!withdrawFor} title={t('Withdraw this award?')} onClose={() => { setWithdrawFor(null); setWithdrawError(''); }} size="sm"
        footer={<>
          <button type="button" className="btn-secondary" onClick={() => { setWithdrawFor(null); setWithdrawError(''); }}>{t('Keep the award')}</button>
          <button type="button" className="btn-primary" disabled={withdrawReason.trim().length < 3} onClick={withdraw}>{t('Withdraw the award')}</button>
        </>}>
        <p className="mb-2 text-sm text-slate-600 dark:text-slate-300">{t('The award for {name} has not taken effect. Withdrawing it cancels only this award; the rest of the cycle stays scheduled. This is recorded in the audit log.', { name: withdrawFor?.name ?? '' })}</p>
        <div className="space-y-3">
          <Field label={t('Reason')} required><textarea className="input min-h-[64px] w-full" value={withdrawReason} maxLength={1000} onChange={(e) => setWithdrawReason(e.target.value)} /></Field>
          <FieldError error={withdrawError} />
        </div>
      </Modal>
      <Modal isOpen={!!reverseFor} title={t('Reverse this award')} onClose={closeReverse} size="sm"
        footer={<>
          <button type="button" className="btn-secondary" onClick={closeReverse}>{t('Keep the award')}</button>
          <button type="button" className="btn-primary" disabled={reason.trim().length < 3 || (lockedNotice && !acknowledged)} onClick={reverse}>{t('Send the reversal for approval')}</button>
        </>}>
        <p className="mb-2 text-sm text-slate-600 dark:text-slate-300">{t('A reversal is a new salary change that takes basic back by the award amount from the date you choose. It is approved in Approvals like any salary change; nothing is deleted.')}</p>
        <div className="space-y-3">
          <Field label={t('Effective date (1st of a month)')} required><input type="date" className="input w-full" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
          <Field label={t('Reason')} required><textarea className="input min-h-[64px] w-full" value={reason} maxLength={1000} onChange={(e) => setReason(e.target.value)} /></Field>
          <FieldError error={error} />
          {lockedNotice && (canApprove ? (
            <label className="flex items-start gap-2 text-xs text-slate-700 dark:text-slate-200">
              <input type="checkbox" className="mt-0.5" checked={acknowledged} onChange={(e) => setAcknowledged(e.target.checked)} />
              <span>{t('I acknowledge this reversal recovers pay from payroll months that are already approved or locked. It is recorded in the audit log.')}</span>
            </label>
          ) : (
            <p className="text-xs text-slate-600 dark:text-slate-300">{t('Ask someone with the compensation approver permission to send this reversal.')}</p>
          ))}
        </div>
      </Modal>
    </div>
  );
}

// ───────────────────────────── create / edit ─────────────────────────────

function CreateCycleModal({ onClose, onCreated }: { onClose: () => void; onCreated: (c: Cycle) => void }) {
  return <CycleFormModal onClose={onClose} onSaved={onCreated} />;
}

function CycleFormModal({ cycle, onClose, onSaved }: { cycle?: Cycle; onClose: () => void; onSaved: (c: Cycle) => void }) {
  const t = useT();
  const { companies, selectedCompanyId } = useCompany();
  const [grades, setGrades] = useState<GradeDto[]>([]);
  const [companyId, setCompanyId] = useState(cycle?.companyId ?? selectedCompanyId ?? companies[0]?.id ?? '');
  const [name, setName] = useState(cycle?.name ?? '');
  const [start, setStart] = useState(cycle?.performancePeriodStart ?? '');
  const [end, setEnd] = useState(cycle?.performancePeriodEnd ?? '');
  const [effective, setEffective] = useState(cycle?.effectiveDate ?? firstOfNextMonth());
  const [cutoff, setCutoff] = useState(cycle?.hireDateCutoff ?? '');
  const [minDays, setMinDays] = useState(String(cycle?.minimumEligibleDays ?? 90));
  const [proration, setProration] = useState<ProrationRule>(cycle?.prorationRule ?? 'EligibleDays');
  const [excludeGrades, setExcludeGrades] = useState<string[]>(cycle?.excludeGradeIds ?? []);
  const [nationality, setNationality] = useState(cycle?.equityIncludesNationality ?? false);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  useEffect(() => { gradesApi.listAll().then(setGrades).catch(() => setGrades([])); }, []);
  const effectiveOk = /^\d{4}-\d{2}-01$/.test(effective);
  const save = async () => {
    setSaving(true); setError('');
    const eligibility: EligibilityInput = {
      hireDateCutoff: cutoff || null, eligibleStatuses: cycle?.eligibleStatuses ?? ['Active'], includeGradeIds: cycle?.includeGradeIds ?? [], excludeGradeIds: excludeGrades,
      includeDepartmentIds: cycle?.includeDepartmentIds ?? [], excludeDepartmentIds: cycle?.excludeDepartmentIds ?? [], minimumEligibleDays: Number(minDays || 0), prorationRule: proration,
    };
    try {
      onSaved(cycle
        ? await compensationApi.updateCycle(cycle.id, { version: cycle.version, name, performancePeriodStart: start, performancePeriodEnd: end, effectiveDate: effective, eligibility, equityIncludesNationality: nationality })
        : await compensationApi.createCycle({ companyId, name, performancePeriodStart: start, performancePeriodEnd: end, effectiveDate: effective, eligibility, equityIncludesNationality: nationality }));
    } catch (err) { setError(serverMessage(err, t)); } finally { setSaving(false); }
  };
  return (
    <Modal isOpen title={cycle ? t('Edit eligibility') : t('New merit cycle')} onClose={onClose} size="lg"
      footer={<>
        <button type="button" className="btn-secondary" onClick={onClose}>{t('Cancel')}</button>
        <button type="button" className="btn-primary" onClick={save} disabled={saving || !name.trim() || !start || !end || !effectiveOk || !companyId}>{saving ? t('Saving…') : cycle ? t('Save') : t('Create cycle')}</button>
      </>}>
      <div className="space-y-3" data-testid="cycle-form">
        {!cycle && (
          <Field label={t('Company')} required hint={t('The cycle is planned in this company’s default currency.')}>
            <select className="input w-full" value={companyId} onChange={(e) => setCompanyId(e.target.value)}>
              {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </Field>
        )}
        <Field label={t('Cycle name')} required><input className="input w-full" value={name} maxLength={160} onChange={(e) => setName(e.target.value)} /></Field>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('Performance period starts')} required><input type="date" className="input w-full" value={start} onChange={(e) => setStart(e.target.value)} /></Field>
          <Field label={t('Performance period ends')} required><input type="date" className="input w-full" value={end} onChange={(e) => setEnd(e.target.value)} /></Field>
        </div>
        <Field label={t('Effective date')} required hint={effectiveOk ? t('Payroll pays a whole month at one rate, so awards start on the 1st.') : t('Choose the 1st of a month.')}>
          <input type="date" className="input w-full" value={effective} onChange={(e) => setEffective(e.target.value)} aria-invalid={!effectiveOk} />
        </Field>
        <Field label={t('Hire-date cutoff (optional)')} hint={t('People who joined after this date are not eligible.')}><input type="date" className="input w-full" value={cutoff} onChange={(e) => setCutoff(e.target.value)} /></Field>
        <Field label={t('Minimum eligible days')}><input className="input w-32" inputMode="numeric" value={minDays} onChange={(e) => setMinDays(e.target.value)} /></Field>
        <fieldset className="space-y-1 text-sm">
          <legend className="mb-1 font-medium text-slate-700 dark:text-slate-300">{t('Proration')}</legend>
          <label className="flex items-center gap-2"><input type="radio" checked={proration === 'EligibleDays'} onChange={() => setProration('EligibleDays')} /> {t('Prorate by eligible days in the period')}</label>
          <label className="flex items-center gap-2"><input type="radio" checked={proration === 'None'} onChange={() => setProration('None')} /> {t('No proration')}</label>
        </fieldset>
        {grades.length > 0 && (
          <fieldset className="space-y-1 text-sm">
            <legend className="mb-1 font-medium text-slate-700 dark:text-slate-300">{t('Exclude grades')}</legend>
            {pickable(grades, ...excludeGrades).map((g) => (
              <label key={g.id} className="flex items-center gap-2">
                <input type="checkbox" checked={excludeGrades.includes(g.id)} onChange={(e) => setExcludeGrades((x) => (e.target.checked ? [...x, g.id] : x.filter((y) => y !== g.id)))} /> {g.name}
              </label>
            ))}
          </fieldset>
        )}
        <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={nationality} onChange={(e) => setNationality(e.target.checked)} /> {t('Also break pay equity down by national / expatriate')}</label>
        <FieldError error={error} />
      </div>
    </Modal>
  );
}
