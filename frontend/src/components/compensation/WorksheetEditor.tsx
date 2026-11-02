'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { Copy, Save, Send, Target } from 'lucide-react';
import { compensationApi, compRefusal, type Blocker, type Line, type LineInput, type Worksheet } from '../../api/compensation';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { Badge, FieldError, PanelState, serverMessage, theadClass, thClass } from '../gl/glUi';
import {
  BlockerList, RangeBar, SyntheticChip, benchmarkStateLabel, changeStatusLabel, changeStatusTone, flagLabel, flagTone, ratingLabel,
  worksheetStatusLabel, worksheetStatusTone,
} from './compUi';
import { BudgetChart } from './BudgetChart';

interface Draft { pct: string; amount: string; justification: string; mode: 'pct' | 'amount' | 'stored' }

const toDraft = (l: Line): Draft => ({
  pct: l.proposedIncrease > 0 ? String(l.proposedPercent) : '',
  amount: l.proposedIncrease > 0 ? l.proposedIncrease.toFixed(2) : '',
  justification: l.justification ?? '',
  mode: 'stored',
});

/**
 * One manager's merit worksheet: the team as pay of record shows it, the guideline cell for each person, a proposal
 * entered as a percent or an amount (either drives the other once saved — the server computes every figure), the
 * prorated award, the new basic, and every flag. Exceptions first: blocking problems are listed above the table.
 */
export function WorksheetEditor({ worksheetId, onChanged }: { worksheetId: string; onChanged?: () => void }) {
  const t = useT();
  const f = useFormat();
  const [sheetId, setSheetId] = useState(worksheetId);
  const [sheet, setSheet] = useState<Worksheet | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [drafts, setDrafts] = useState<Record<number, Draft>>({});
  const [overspend, setOverspend] = useState('');
  const [busy, setBusy] = useState<'save' | 'submit' | 'scenario' | 'primary' | null>(null);
  const [actionError, setActionError] = useState('');
  const [refusal, setRefusal] = useState<Blocker[]>([]);
  const [notice, setNotice] = useState('');
  const [newScenario, setNewScenario] = useState('');

  useEffect(() => setSheetId(worksheetId), [worksheetId]);

  const adopt = useCallback((w: Worksheet) => {
    setSheet(w);
    setDrafts(Object.fromEntries(w.lines.map((l) => [l.employeeId, toDraft(l)])));
    setOverspend(w.overspendJustification ?? '');
  }, []);

  const load = useCallback(async () => {
    setError(null);
    try { adopt(await compensationApi.worksheet(sheetId)); } catch (err) { setError(serverMessage(err, t)); }
  }, [sheetId, adopt, t]);
  useEffect(() => { setSheet(null); void load(); }, [load]);

  const dirty = useMemo(() => Object.values(drafts).some((d) => d.mode !== 'stored') || (sheet != null && (sheet.overspendJustification ?? '') !== overspend)
    || (sheet?.lines.some((l) => (drafts[l.employeeId]?.justification ?? '') !== (l.justification ?? '')) ?? false), [drafts, overspend, sheet]);

  const lineInputs = (): LineInput[] => (sheet?.lines ?? []).map((l) => {
    const d = drafts[l.employeeId] ?? toDraft(l);
    const num = (s: string) => (s.trim() === '' ? 0 : Number(s));
    return {
      employeeId: l.employeeId,
      // Unchanged lines are sent the way they were entered, so a percent stays a percent (the guideline tests it as entered).
      proposedIncrease: d.mode === 'pct' ? null : d.mode === 'amount' ? num(d.amount) : l.proposedAsPercent ? null : l.proposedIncrease,
      proposedPercent: d.mode === 'pct' ? num(d.pct) : d.mode === 'stored' && l.proposedAsPercent ? l.proposedPercent : null,
      justification: d.justification.trim() || null,
    };
  });

  const invalid = (sheet?.lines ?? []).some((l) => {
    const d = drafts[l.employeeId];
    if (!d) return false;
    const v = d.mode === 'pct' ? d.pct : d.mode === 'amount' ? d.amount : '0';
    return v.trim() !== '' && (Number.isNaN(Number(v)) || Number(v) < 0);
  });

  const run = async (kind: 'save' | 'submit', work: () => Promise<Worksheet>, done: string) => {
    setBusy(kind); setActionError(''); setRefusal([]); setNotice('');
    try {
      const w = await work();
      adopt(w);
      setNotice(done);
      onChanged?.();
    } catch (err) {
      const r = compRefusal(err);
      if (r?.blockers.length) setRefusal(r.blockers);
      setActionError(r?.message ?? serverMessage(err, t));
      if (r?.error === 'conflict') void load();
    } finally { setBusy(null); }
  };

  const save = () => sheet && run('save', () => compensationApi.saveWorksheet(sheet.id, { version: sheet.version, overspendJustification: overspend.trim() || null, lines: lineInputs() }),
    t('Worksheet saved.'));

  const submit = () => sheet && run('submit', async () => {
    let current = sheet;
    if (dirty) current = await compensationApi.saveWorksheet(sheet.id, { version: sheet.version, overspendJustification: overspend.trim() || null, lines: lineInputs() });
    return compensationApi.submitWorksheet(current.id, current.version);
  }, t('Submitted for approval. You will see the decision here.'));

  const fillTargets = () => {
    if (!sheet) return;
    setDrafts((prev) => Object.fromEntries(sheet.lines.map((l) => [l.employeeId,
      l.guidelineTargetPercent != null ? { ...(prev[l.employeeId] ?? toDraft(l)), pct: String(l.guidelineTargetPercent), amount: '', mode: 'pct' as const } : prev[l.employeeId] ?? toDraft(l)])));
  };

  const addScenario = async () => {
    if (!sheet || !newScenario.trim()) return;
    setBusy('scenario'); setActionError('');
    try {
      const created = await compensationApi.createScenario(sheet.cycleId, { managerEmployeeId: sheet.managerEmployeeId, scenarioName: newScenario.trim(), copyFromWorksheetId: sheet.id });
      setNewScenario('');
      setSheetId(created.id);
    } catch (err) { setActionError(serverMessage(err, t)); } finally { setBusy(null); }
  };

  const makePrimary = async () => {
    if (!sheet) return;
    setBusy('primary'); setActionError('');
    try { adopt(await compensationApi.makePrimary(sheet.id, sheet.version)); onChanged?.(); } catch (err) { setActionError(serverMessage(err, t)); } finally { setBusy(null); }
  };

  const set = (id: number, patch: Partial<Draft>) => setDrafts((prev) => ({ ...prev, [id]: { ...(prev[id] ?? { pct: '', amount: '', justification: '', mode: 'stored' }), ...patch } }));

  return (
    <PanelState loading={!sheet && !error} error={error} onRetry={load} empty={false} emptyLabel="">
      {sheet && (
        <section className="space-y-4" aria-labelledby="worksheet-heading" data-testid="worksheet">
          <header className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <h3 id="worksheet-heading" className="text-base font-semibold text-slate-900 dark:text-white">
                {t('Merit worksheet: {manager}', { manager: sheet.managerName })}
              </h3>
              <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
                {t('{cycle} · effective {date} · amounts in {currency} per month', { cycle: sheet.cycleName, date: f.date(sheet.effectiveDate), currency: sheet.currency })}
              </p>
              <div className="mt-2 flex flex-wrap items-center gap-2">
                <Badge tone={worksheetStatusTone(sheet.status)}>{worksheetStatusLabel(t, sheet.status)}</Badge>
                <Badge tone="slate">{t('Revision {n}', { n: sheet.revision })}</Badge>
                {sheet.isPrimary ? <Badge tone="sapphire">{t('Scenario for submission')}</Badge> : <Badge tone="slate">{t('What-if scenario (never submitted)')}</Badge>}
                {sheet.overspend && <Badge tone="rose">{t('Over budget')}</Badge>}
              </div>
            </div>
            <div className="text-end">
              <p className="text-xs text-slate-500 dark:text-slate-400">{t('Proposed this worksheet')}</p>
              <p className="text-xl font-semibold tabular-nums text-slate-900 dark:text-white" data-testid="worksheet-total">{f.money(sheet.proposedTotal, sheet.currency)}</p>
            </div>
          </header>

          {sheet.scenarios.length > 0 && (
            <div className="flex flex-wrap items-end gap-2 text-xs">
              <label className="flex flex-col gap-1">
                <span className="font-medium text-slate-600 dark:text-slate-300">{t('Scenario')}</span>
                <select className="input h-8 text-xs" value={sheet.id} onChange={(e) => setSheetId(e.target.value)} aria-label={t('Scenario')}>
                  {sheet.scenarios.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.isPrimary ? t('{name} (submitted scenario)', { name: s.scenarioName }) : s.scenarioName}
                    </option>
                  ))}
                </select>
              </label>
              {sheet.canEdit && (
                <>
                  <label className="flex flex-col gap-1">
                    <span className="font-medium text-slate-600 dark:text-slate-300">{t('New scenario name')}</span>
                    <input className="input h-8 w-40 text-xs" value={newScenario} maxLength={80} onChange={(e) => setNewScenario(e.target.value)} />
                  </label>
                  <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={!newScenario.trim() || busy != null} onClick={addScenario}>
                    <Copy className="h-3.5 w-3.5" aria-hidden /> {t('Copy into a new scenario')}
                  </button>
                  {!sheet.isPrimary && (
                    <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={busy != null} onClick={makePrimary}>
                      {t('Use this scenario for submission')}
                    </button>
                  )}
                </>
              )}
            </div>
          )}

          {sheet.status === 'Returned' && sheet.decisionComments && (
            <div role="alert" className="rounded-lg border border-rose-300 bg-rose-50 px-3 py-2 text-sm text-rose-800 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200" data-testid="returned-comments">
              <p className="font-semibold">{t('Returned by the approver')}</p>
              <p>{sheet.decisionComments}</p>
            </div>
          )}
          {sheet.status === 'Submitted' && (
            <p className="rounded-lg bg-violet-50 px-3 py-2 text-xs text-violet-800 dark:bg-violet-500/10 dark:text-violet-200">
              {t('This worksheet is locked while it waits for approval in Approvals. An approver can return it to you with comments.')}
            </p>
          )}
          {notice && <p role="status" className="rounded-lg bg-emerald-50 px-3 py-2 text-xs text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-200">{notice}</p>}
          <FieldError error={actionError} />
          <BlockerList blockers={refusal.length ? refusal : sheet.canEdit ? sheet.blockers : []} t={t} />

          {sheet.budgetImpact.length > 0 && (
            <div className="surface p-3">
              <h4 className="mb-2 text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Budget impact')}</h4>
              <BudgetChart budgets={sheet.budgetImpact} currency={sheet.currency} />
              <ul className="mt-2 space-y-1 text-xs text-slate-600 dark:text-slate-300">
                {sheet.budgetImpact.map((b) => (
                  <li key={b.departmentId}>
                    {b.overspent
                      ? t('{dept}: {proposed} proposed against a pool of {pool} — {over} over budget.', { dept: b.departmentName, proposed: f.money(b.proposed, sheet.currency), pool: f.money(b.pool, sheet.currency), over: f.money(-b.remaining, sheet.currency) })
                      : t('{dept}: {proposed} proposed against a pool of {pool} — {left} left.', { dept: b.departmentName, proposed: f.money(b.proposed, sheet.currency), pool: f.money(b.pool, sheet.currency), left: f.money(b.remaining, sheet.currency) })}
                  </li>
                ))}
              </ul>
            </div>
          )}

          {sheet.canEdit && (
            <div className="flex flex-wrap gap-2">
              <button type="button" className="btn-secondary h-9 px-3 text-sm" onClick={fillTargets} disabled={busy != null}>
                <Target className="h-4 w-4" aria-hidden /> {t('Fill guideline targets')}
              </button>
            </div>
          )}

          <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-white/10">
            <table className="min-w-[1100px] w-full text-sm" data-testid="worksheet-lines">
              <thead className={theadClass}>
                <tr>
                  <th className={thClass}>{t('Employee')}</th>
                  <th className={thClass}>{t('Pay now')}</th>
                  <th className={thClass}>{t('Grade position')}</th>
                  <th className={thClass}>{t('Rating and guideline')}</th>
                  <th className={thClass}>{t('Increase %')}</th>
                  <th className={thClass}>{t('Increase amount')}</th>
                  <th className={thClass}>{t('Award and new pay')}</th>
                  <th className={thClass}>{t('Market')}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {sheet.lines.map((l) => {
                  const d = drafts[l.employeeId] ?? toDraft(l);
                  const foreign = l.payCurrency !== sheet.currency;
                  const sameGradeCurrency = l.gradeCurrency === l.payCurrency && l.gradeMin != null && l.gradeMax != null && l.gradeMax > l.gradeMin;
                  const newPos = sameGradeCurrency ? (l.newGross - (l.gradeMin as number)) / ((l.gradeMax as number) - (l.gradeMin as number)) : null;
                  return (
                    <tr key={l.employeeId} data-employee-code={l.employeeCode} className="align-top">
                      <td className="px-3 py-2">
                        <div className="font-medium text-slate-900 dark:text-white">{l.employeeName}</div>
                        <div className="text-caption text-slate-500 dark:text-slate-400">{l.employeeCode} · {l.designationName} · {l.departmentName}</div>
                        {l.assignedByHr && <div className="mt-1"><Badge tone="violet">{t('Assigned by HR')}</Badge></div>}
                        <div className="mt-1 flex flex-wrap gap-1">
                          {l.flags.filter((x) => !x.endsWith('_benchmark')).map((x) => <Badge key={x} tone={flagTone(x)}>{flagLabel(t, x)}</Badge>)}
                        </div>
                        {l.changeRequestId && <div className="mt-1"><Badge tone={changeStatusTone(l.changeStatus)}>{changeStatusLabel(t, l.changeStatus)}</Badge></div>}
                      </td>
                      <td className="px-3 py-2 tabular-nums">
                        <div>{t('Basic {amount}', { amount: f.money(l.currentBasic, l.payCurrency) })}</div>
                        <div className="text-caption text-slate-500 dark:text-slate-400">{t('Gross {amount}', { amount: f.money(l.currentGross, l.payCurrency) })}</div>
                        {foreign && l.fxRate != null && (
                          <div className="text-caption text-slate-500 dark:text-slate-400">
                            {t('= {amount} at {rate}', { amount: f.money(l.currentBasicCycle, sheet.currency), rate: f.number(l.fxRate, { maximumFractionDigits: 8 }) })}
                          </div>
                        )}
                      </td>
                      <td className="px-3 py-2 tabular-nums">
                        <div>{l.compaRatio != null ? t('Compa-ratio {value}', { value: f.number(l.compaRatio, { minimumFractionDigits: 2, maximumFractionDigits: 4 }) }) : t('No grade range')}</div>
                        <RangeBar current={l.rangePosition} proposed={newPos}
                          label={t('Range position {now}, after the award {after}', { now: f.percent((l.rangePosition ?? 0) * 100), after: newPos == null ? '—' : f.percent(newPos * 100) })} />
                        {l.gradeMid != null && (
                          <div className="mt-1 text-caption text-slate-500 dark:text-slate-400">
                            {`${l.gradeName}: ${f.money(l.gradeMin, l.gradeCurrency)} – ${f.money(l.gradeMid, l.gradeCurrency)} – ${f.money(l.gradeMax, l.gradeCurrency)}`}
                          </div>
                        )}
                      </td>
                      <td className="px-3 py-2">
                        <div>{ratingLabel(t, l.rating)}</div>
                        <div className="text-caption text-slate-500 dark:text-slate-400">
                          {l.guidelineTargetPercent != null
                            ? t('Guideline {min} – {max}, target {target}', { min: f.percent(l.guidelineMinPercent, 2), max: f.percent(l.guidelineMaxPercent, 2), target: f.percent(l.guidelineTargetPercent, 2) })
                            : t('No guideline for this rating')}
                        </div>
                      </td>
                      <td className="px-3 py-2">
                        {sheet.canEdit ? (
                          <input className="input h-8 w-20 text-end tabular-nums" inputMode="decimal" aria-label={t('Increase % for {name}', { name: l.employeeName })}
                            value={d.mode === 'amount' ? '' : d.pct} placeholder={d.mode === 'amount' ? t('From the amount') : '0'}
                            onChange={(e) => set(l.employeeId, { pct: e.target.value, mode: 'pct' })} />
                        ) : <span className="tabular-nums">{f.percent(l.proposedPercent, 2)}</span>}
                      </td>
                      <td className="px-3 py-2">
                        {sheet.canEdit ? (
                          <input className="input h-8 w-28 text-end tabular-nums" inputMode="decimal" aria-label={t('Increase amount for {name}', { name: l.employeeName })}
                            value={d.mode === 'pct' ? '' : d.amount} placeholder={d.mode === 'pct' ? t('From the %') : '0.00'}
                            onChange={(e) => set(l.employeeId, { amount: e.target.value, mode: 'amount' })} />
                        ) : <span className="tabular-nums">{f.money(l.proposedIncrease, sheet.currency)}</span>}
                        {(l.outOfGuideline || d.justification) && (
                          sheet.canEdit ? (
                            <textarea className="input mt-1 min-h-[56px] w-56 text-xs" maxLength={2000} aria-label={t('Justification for {name}', { name: l.employeeName })}
                              placeholder={t('Why this is outside the guideline')} value={d.justification} onChange={(e) => set(l.employeeId, { justification: e.target.value })} />
                          ) : <p className="mt-1 max-w-[14rem] text-caption text-slate-600 dark:text-slate-300">{l.justification}</p>
                        )}
                      </td>
                      <td className="px-3 py-2 tabular-nums">
                        <div className="font-medium">{t('Award {amount}', { amount: f.money(l.proratedIncreasePay, l.payCurrency) })}</div>
                        {l.prorationFactor < 1 && (
                          <div className="text-caption text-amber-700 dark:text-amber-300">
                            {t('Prorated: {days} eligible days ({factor})', { days: l.eligibleDays, factor: f.percent(l.prorationFactor * 100, 2) })}
                          </div>
                        )}
                        <div className="text-caption text-slate-500 dark:text-slate-400">{t('New basic {amount}', { amount: f.money(l.newBasic, l.payCurrency) })}</div>
                        {l.newCompaRatio != null && <div className="text-caption text-slate-500 dark:text-slate-400">{t('New compa-ratio {value}', { value: f.number(l.newCompaRatio, { minimumFractionDigits: 2, maximumFractionDigits: 4 }) })}</div>}
                        {l.blockingMessages.map((m) => <p key={m} className="mt-1 max-w-[16rem] text-caption text-rose-700 dark:text-rose-300">{m}</p>)}
                        {l.applyBlockedReason && <p className="mt-1 max-w-[16rem] text-caption text-rose-700 dark:text-rose-300">{l.applyBlockedReason}</p>}
                      </td>
                      <td className="px-3 py-2">
                        <Badge tone={l.benchmark.state === 'current' ? 'emerald' : l.benchmark.state === 'missing' ? 'slate' : 'amber'} title={l.benchmark.detail ?? undefined}>
                          {benchmarkStateLabel(t, l.benchmark.state)}
                        </Badge>
                        {l.benchmark.isSynthetic && <div className="mt-1"><SyntheticChip t={t} /></div>}
                        {l.benchmark.p50Cycle != null && (
                          <div className="mt-1 text-caption tabular-nums text-slate-600 dark:text-slate-300">
                            {t('P50 {amount} · at {position} of P50', { amount: f.money(l.benchmark.p50Cycle, sheet.currency), position: f.percent((l.benchmark.position ?? 0) * 100) })}
                          </div>
                        )}
                        {l.benchmark.detail && <p className="mt-1 max-w-[12rem] text-caption text-slate-500 dark:text-slate-400">{l.benchmark.detail}</p>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          {sheet.excluded.length > 0 && (
            <details className="text-xs text-slate-600 dark:text-slate-300">
              <summary className="cursor-pointer font-medium">{t('{count, plural, one {# direct report is not eligible} other {# direct reports are not eligible}}', { count: sheet.excluded.length })}</summary>
              <ul className="mt-1 list-disc space-y-0.5 ps-5">
                {sheet.excluded.map((x) => <li key={x.employeeId}>{`${x.name}: ${x.reason}`}</li>)}
              </ul>
            </details>
          )}

          {sheet.canEdit && (
            <div className="space-y-3">
              <label className="block text-sm">
                <span className="mb-1 block font-medium text-slate-700 dark:text-slate-300">{t('Overspend justification')}</span>
                <span className="mb-1 block text-caption text-slate-500 dark:text-slate-400">{t('Needed only when this worksheet takes a department over its pool. Approvers see it with the request.')}</span>
                <textarea className="input min-h-[64px] w-full text-sm" maxLength={2000} value={overspend} onChange={(e) => setOverspend(e.target.value)} />
              </label>
              <div className="flex flex-wrap justify-end gap-2">
                <button type="button" className="btn-secondary" onClick={save} disabled={busy != null || invalid || !dirty}>
                  <Save className="h-4 w-4" aria-hidden /> {busy === 'save' ? t('Saving…') : t('Save worksheet')}
                </button>
                {sheet.isPrimary && (
                  <button type="button" className="btn-primary" onClick={submit} disabled={busy != null || invalid}>
                    <Send className="h-4 w-4" aria-hidden /> {busy === 'submit' ? t('Submitting…') : t('Submit for approval')}
                  </button>
                )}
              </div>
            </div>
          )}
        </section>
      )}
    </PanelState>
  );
}
