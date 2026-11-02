'use client';

import { useEffect, useMemo, useState } from 'react';
import { BarChart3, ClipboardCheck, Layers3, ListChecks, Target, UserPlus } from 'lucide-react';
import { successionApi, type CriticalPosition } from '../api/succession';
import { RovingTabList, TabPanel, type RovingTabItem } from '../components/ui/RovingTabs';
import { CalibrationPanel } from '../components/succession/CalibrationPanel';
import { KpiTiles } from '../components/succession/KpiTiles';
import { MyNominationsPanel } from '../components/succession/NominationsPanel';
import { PlansPanel } from '../components/succession/PlansPanel';
import { PoolsPanel } from '../components/succession/PoolsPanel';
import { PositionList, PositionsPanel } from '../components/succession/PositionsPanel';
import { ReportsPanel } from '../components/succession/ReportsPanel';
import { SectionCard, SuccessionGate, useSuccessionAccess } from '../components/succession/SuccessionParts';
import { useLocale } from '../contexts/LocaleContext';
import { sortGapsFirst } from '../lib/succession';

type Tab = 'positions' | 'calibration' | 'pools' | 'nominations' | 'plans' | 'reports';

/**
 * Succession and career development (SUC), gaps first: the headline figures (each with its definition and its rows),
 * then the critical positions with what is missing, then talent review, pools, nominations, development plans and the
 * trend. Each audience sees only what its permissions open — a line manager sees their nominations (stage masked) and
 * their team's plans, never a slate or a 9-box. The tab is in the URL (?tab=).
 */
export function SuccessionPage() {
  return (
    <SuccessionGate>
      <SuccessionBody />
    </SuccessionGate>
  );
}

function SuccessionBody() {
  const { t } = useLocale();
  const access = useSuccessionAccess();
  const tabs = useMemo<Array<RovingTabItem<Tab>>>(() => [
    ...(access.read ? [{ id: 'positions' as Tab, label: t('suc.tab.positions'), icon: Target, testId: 'suc-tab-positions' }] : []),
    ...(access.slatesRead ? [{ id: 'calibration' as Tab, label: t('suc.tab.calibration'), icon: ClipboardCheck, testId: 'suc-tab-calibration' }] : []),
    ...(access.read ? [{ id: 'pools' as Tab, label: t('suc.tab.pools'), icon: Layers3, testId: 'suc-tab-pools' }] : []),
    ...(access.nominate ? [{ id: 'nominations' as Tab, label: t('suc.tab.nominations'), icon: UserPlus, testId: 'suc-tab-nominations' }] : []),
    ...(access.devplansRead ? [{ id: 'plans' as Tab, label: t('suc.tab.plans'), icon: ListChecks, testId: 'suc-tab-plans' }] : []),
    ...(access.reportsRead ? [{ id: 'reports' as Tab, label: t('suc.tab.reports'), icon: BarChart3, testId: 'suc-tab-reports' }] : []),
  ], [t, access.read, access.slatesRead, access.nominate, access.devplansRead, access.reportsRead]);
  const [tab, setTab] = useState<Tab | null>(null);
  const [figuresVersion, setFiguresVersion] = useState(0); // a designation changes the headline figures
  const active = tab && tabs.some((x) => x.id === tab) ? tab : tabs[0]?.id ?? null;

  useEffect(() => {
    const wanted = new URLSearchParams(window.location.search).get('tab') as Tab | null;
    if (wanted && tabs.some((x) => x.id === wanted)) setTab(wanted);
  }, [tabs]);

  const change = (next: Tab) => {
    setTab(next);
    const url = new URL(window.location.href);
    url.searchParams.set('tab', next);
    window.history.replaceState(null, '', url.toString());
  };

  return (
    <div className="space-y-5" data-testid="suc-workspace">
      <header>
        <h1 className="text-lg font-semibold text-slate-800 dark:text-slate-100">{t('suc.title')}</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('suc.subtitle')}</p>
      </header>

      {access.reportsRead && <KpiTiles canSeeNames={access.slatesRead} refreshKey={figuresVersion} />}
      {(access.self || access.read) && <AccountablePositions />}

      {tabs.length === 0 && <p className="text-sm text-slate-600 dark:text-slate-300" data-testid="suc-forbidden">{t('suc.forbidden')}</p>}
      {tabs.length > 0 && active && (
        <>
          <RovingTabList items={tabs} activeId={active} onChange={change} idPrefix="suc" label={t('suc.tabs')} />
          <TabPanel idPrefix="suc" tabId={active}>
            {active === 'positions' && <PositionsPanel canManage={access.positionsManage} onChanged={() => setFiguresVersion((v) => v + 1)} />}
            {active === 'calibration' && <CalibrationPanel access={access} />}
            {active === 'pools' && <PoolsPanel canManage={access.poolsManage} canSeeNames={access.slatesRead} />}
            {active === 'nominations' && <MyNominationsPanel />}
            {active === 'plans' && <PlansPanel access={access} />}
            {active === 'reports' && <ReportsPanel />}
          </TabPanel>
        </>
      )}
    </div>
  );
}

/** The positions the caller is the accountable executive for — shown only when there are some. */
function AccountablePositions() {
  const { t } = useLocale();
  const [rows, setRows] = useState<CriticalPosition[] | null>(null);
  useEffect(() => {
    // Quietly absent on failure: this is a convenience list, the positions tab is the full one.
    successionApi.myPositions().then((r) => setRows(sortGapsFirst(r))).catch(() => setRows([]));
  }, []);
  if (!rows || rows.length === 0) return null;
  return (
    <SectionCard title={t('suc.mine.positionsTitle')} description={t('suc.mine.positionsHint')} testId="suc-accountable">
      <PositionList rows={rows} rowTestId="suc-accountable-row" />
    </SectionCard>
  );
}
