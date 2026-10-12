'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { BarChart3, ClipboardList, Layers, Scale } from 'lucide-react';
import { compensationApi, COMP_PERMISSIONS, type MyWorksheets } from '../api/compensation';
import { BenchmarksPanel } from '../components/compensation/BenchmarksPanel';
import { CycleWorkspace } from '../components/compensation/CycleWorkspace';
import { EquityPanel } from '../components/compensation/EquityPanel';
import { WorksheetEditor } from '../components/compensation/WorksheetEditor';
import { Badge, PanelState, serverMessage } from '../components/gl/glUi';
import { cycleStatusLabel, worksheetStatusLabel, worksheetStatusTone } from '../components/compensation/compUi';
import { SectionHeader } from '../components/SectionHeader';
import { RovingTabList, TabPanel } from '../components/ui/RovingTabs';
import { useAuth } from '../contexts/AuthContext';
import { useFormat } from '../hooks/useFormat';
import { useT } from '../hooks/useT';

type Tab = 'worksheets' | 'cycles' | 'benchmarks' | 'equity';

/**
 * Merit planning (CMP). One page, one tab per audience: a line manager sees their worksheets; HR runs cycles; Finance
 * reads budgets and totals and decides worksheet steps in Approvals; benchmark and pay-equity tabs need their own keys.
 * Approved awards reach pay only through the governed salary-change path — this page never writes a salary.
 */
export function CompensationPage() {
  const t = useT();
  const { hasPermission } = useAuth();
  const tabs = useMemo(() => {
    const list: { id: Tab; label: string; icon: React.ElementType<{ className?: string }> }[] = [];
    if (hasPermission(COMP_PERMISSIONS.plan)) list.push({ id: 'worksheets', label: t('My worksheets'), icon: ClipboardList });
    if ([COMP_PERMISSIONS.manage, COMP_PERMISSIONS.read, COMP_PERMISSIONS.approve].some((p) => hasPermission(p))) list.push({ id: 'cycles', label: t('Merit cycles'), icon: BarChart3 });
    if ([COMP_PERMISSIONS.benchmarks, COMP_PERMISSIONS.read].some((p) => hasPermission(p))) list.push({ id: 'benchmarks', label: t('Pay benchmarks'), icon: Layers });
    if (hasPermission(COMP_PERMISSIONS.equity)) list.push({ id: 'equity', label: t('Pay equity'), icon: Scale });
    return list;
  }, [hasPermission, t]);
  const [tab, setTab] = useState<Tab | null>(null);
  const active = tab && tabs.some((x) => x.id === tab) ? tab : tabs[0]?.id ?? null;

  return (
    <div className="space-y-5">
      <SectionHeader title={t('Merit planning')}
        description={t('Plan merit increases within budget and guidelines, approve them with separation of duties, and send them to payroll as dated salary changes.')} />
      {active && (
        <>
          <RovingTabList items={tabs} activeId={active} onChange={(id) => setTab(id)} idPrefix="compensation" label={t('Merit planning sections')} variant="underline" />
          <TabPanel idPrefix="compensation" tabId={active}>
            {active === 'worksheets' && <MyWorksheetsTab />}
            {active === 'cycles' && <CycleWorkspace />}
            {active === 'benchmarks' && <BenchmarksPanel />}
            {active === 'equity' && <EquityPanel />}
          </TabPanel>
        </>
      )}
    </div>
  );
}

function MyWorksheetsTab() {
  const t = useT();
  const f = useFormat();
  const [data, setData] = useState<MyWorksheets | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const load = useCallback(async () => {
    setError(null);
    try {
      const d = await compensationApi.myWorksheets();
      setData(d);
      const openable = d.worksheets.filter((w) => w.companyAccess !== false);
      setOpen((o) => o ?? openable.find((w) => w.isPrimary && (w.status === 'Draft' || w.status === 'Returned'))?.id ?? openable.find((w) => w.isPrimary)?.id ?? null);
    } catch (err) { setError(serverMessage(err, t)); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);
  const primaries = (data?.worksheets ?? []).filter((w) => w.isPrimary);
  return (
    <div className="space-y-4">
      <PanelState loading={!data && !error} error={error} onRetry={load} empty={primaries.length === 0}
        emptyLabel={t('You have no merit worksheet. One opens for you when HR starts planning a cycle that includes your direct reports.')}>
        <ul className="grid gap-2 md:grid-cols-2" data-testid="my-worksheets">
          {primaries.map((w) => {
            const cycle = data?.cycles.find((c) => c.id === w.cycleId);
            return (
              <li key={w.id}>
                <button type="button" onClick={() => setOpen(w.id)} aria-current={open === w.id} disabled={w.companyAccess === false}
                  className={`surface w-full p-3 text-start ${open === w.id ? 'ring-2 ring-sapphire/40' : ''}`}>
                  <span className="flex items-center justify-between gap-2">
                    <span className="font-medium text-slate-900 dark:text-white">{t('{count, plural, one {# person} other {# people}}', { count: w.lineCount })}</span>
                    <Badge tone={worksheetStatusTone(w.status)}>{worksheetStatusLabel(t, w.status)}</Badge>
                  </span>
                  <span className="mt-1 block text-xs text-slate-500 dark:text-slate-400">
                    {cycle ? `${cycle.name} · ${cycleStatusLabel(t, cycle.status)}` : ''}
                  </span>
                  <span className="mt-1 block text-sm tabular-nums">{t('Proposed {amount}', { amount: f.money(w.proposedTotal, cycle?.currency ?? null) })}</span>
                  {w.companyAccess === false && (
                    <span className="mt-1 block text-xs text-amber-800 dark:text-amber-300">
                      {t('This team is in {company}, which your login cannot open. Ask HR to give you access to it, or to complete the worksheet for you.', { company: cycle?.companyName ?? '' })}
                    </span>
                  )}
                </button>
              </li>
            );
          })}
        </ul>
      </PanelState>
      {open && <div className="surface p-4"><WorksheetEditor worksheetId={open} onChanged={load} /></div>}
    </div>
  );
}
