'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { BarChart3, BookOpen, ClipboardList, Route, UserPlus } from 'lucide-react';
import { RovingTabList, TabPanel, type RovingTabItem } from '../components/ui/RovingTabs';
import { LearningGate } from '../components/learning/LearningParts';
import { LearningReports } from '../components/learning/LearningReports';
import { CourseCatalog } from '../components/learning/CourseCatalog';
import { PathsPanel } from '../components/learning/PathsPanel';
import { AssignPanel } from '../components/learning/AssignPanel';
import { RecordsPanel } from '../components/learning/RecordsPanel';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';

type Tab = 'overview' | 'courses' | 'paths' | 'assign' | 'records';

/** What a report drill-down opens in Records. */
export interface RecordsFilter { status?: string; pathId?: string; assignmentId?: string }

/**
 * Learning (LRN): one screen for HR, a manager and an HR officer, each seeing only the tabs their permissions open —
 * Overview (learning.report: KPIs with definitions, drill-down, CSV), Courses and Paths (learning.read / manage),
 * Assign (learning.assign; standing rules need learning.manage) and Records (assignments, attempts, certificates,
 * corrections). The tab is in the URL (?tab=), so a drill-down or a refresh lands where it was.
 */
export function LearningPage() {
  return (
    <LearningGate>
      <LearningBody />
    </LearningGate>
  );
}

function LearningBody() {
  const { t } = useLocale();
  const { hasPermission } = useAuth();
  const can = useCallback((...keys: string[]) => keys.some((k) => hasPermission(k)), [hasPermission]);
  const tabs = useMemo<RovingTabItem<Tab>[]>(() => [
    ...(can('learning.report') ? [{ id: 'overview' as Tab, label: t('Overview'), icon: BarChart3 }] : []),
    ...(can('learning.read', 'learning.manage') ? [{ id: 'courses' as Tab, label: t('Courses'), icon: BookOpen }] : []),
    ...(can('learning.read', 'learning.manage', 'learning.assign') ? [{ id: 'paths' as Tab, label: t('Learning paths'), icon: Route }] : []),
    ...(can('learning.assign', 'learning.manage') ? [{ id: 'assign' as Tab, label: t('Assign'), icon: UserPlus }] : []),
    ...(can('learning.assign', 'learning.report', 'learning.records.correct') ? [{ id: 'records' as Tab, label: t('Records'), icon: ClipboardList }] : []),
  ], [t, can]);
  const [tab, setTab] = useState<Tab>(tabs[0]?.id ?? 'courses');
  const [filter, setFilter] = useState<RecordsFilter>({});

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

  const drill = (next: RecordsFilter) => {
    setFilter(next);
    change('records');
  };

  return (
    <div className="space-y-5" data-testid="learning-hr">
      <header>
        <h1 className="text-lg font-semibold text-slate-800 dark:text-slate-100">{t('Learning')}</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('Courses, learning paths, mandatory training and certificates — who has done what, and what is due.')}</p>
      </header>
      <RovingTabList items={tabs} activeId={tab} onChange={change} idPrefix="learning" label={t('Learning sections')} />
      {tabs.some((x) => x.id === tab) && (
        <TabPanel idPrefix="learning" tabId={tab}>
          {tab === 'overview' && <LearningReports onDrill={drill} />}
          {tab === 'courses' && <CourseCatalog canManage={can('learning.manage')} />}
          {tab === 'paths' && <PathsPanel canManage={can('learning.manage')} />}
          {tab === 'assign' && <AssignPanel canAssign={can('learning.assign')} canManageRules={can('learning.manage')} />}
          {tab === 'records' && <RecordsPanel filter={filter} canCorrect={can('learning.records.correct')} canAssign={can('learning.assign')} />}
        </TabPanel>
      )}
    </div>
  );
}
