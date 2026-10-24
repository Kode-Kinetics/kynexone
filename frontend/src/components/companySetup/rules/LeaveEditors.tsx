'use client';

import { useRef, useState } from 'react';
import { useT } from '../../../hooks/useT';
import { msg } from '../../../i18n/translations';
import type { LeaveEditorChange } from '../../../views/LeavePage';
import { RovingTabList, TabPanel } from '../../ui/RovingTabs';
import { EmbeddedHolidayCalendars, EmbeddedLeavePolicies, EmbeddedLeaveTypes } from './EmbeddedEditors';
import { useDirtyReporter, useKeyboardInitiated } from './rulesShared';
import styles from './motion.module.css';

type LeaveTab = 'policies' | 'holidays' | 'types';
const TABS: { id: LeaveTab; label: string }[] = [
  { id: 'policies', label: msg('Leave policies') },
  { id: 'holidays', label: msg('Holiday calendars') },
  { id: 'types', label: msg('Leave types') },
];

/**
 * The real Leave policies, Holiday calendars and Leave types editors (views/LeavePage), in their narrow
 * `embedded` layout. They save live through their own APIs. With `companyId`, leave types and policies are this
 * company's own (new ones belong to it), and holiday calendars show its own beside the workspace-wide ones. An open, unsaved form
 * is reported under its editor's name (rulesShared DirtyRegistry), so leaving the part can ask first.
 */
export function LeaveEditors({ companyId, onSaved, idPrefix = 'rules-leave' }: { companyId?: string; onSaved?: (change: LeaveEditorChange) => void; idPrefix?: string }) {
  const t = useT();
  const [tab, setTab] = useState<LeaveTab>('policies');
  const keyboard = useKeyboardInitiated();
  const animate = useRef(false);
  const reportPolicies = useDirtyReporter(TABS[0].label);
  const reportHolidays = useDirtyReporter(TABS[1].label);
  const reportTypes = useDirtyReporter(TABS[2].label);
  const choose = (next: LeaveTab) => { animate.current = !keyboard(); setTab(next); };
  return (
    <div className="space-y-3">
      <RovingTabList items={TABS.map(item => ({ id: item.id, label: t(item.label), testId: `leave-tab-${item.id}` }))} activeId={tab} onChange={choose} idPrefix={idPrefix} label={t('Leave lists')} />
      <TabPanel idPrefix={idPrefix} tabId={tab} className={`rounded-xl ${animate.current ? styles.fadeIn : ''}`} key={tab}>
        {tab === 'policies' && <EmbeddedLeavePolicies embedded onDirtyChange={reportPolicies} onSaved={onSaved} companyId={companyId} />}
        {tab === 'holidays' && <EmbeddedHolidayCalendars embedded onDirtyChange={reportHolidays} onSaved={onSaved} companyId={companyId} />}
        {tab === 'types' && <EmbeddedLeaveTypes embedded onDirtyChange={reportTypes} onSaved={onSaved} companyId={companyId} />}
      </TabPanel>
    </div>
  );
}
