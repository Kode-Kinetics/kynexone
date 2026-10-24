'use client';

import dynamic from 'next/dynamic';
import styles from './motion.module.css';

/**
 * The existing Setup editors, loaded on first use (their screens are large) with a skeleton in the editor's final
 * shape, so nothing moves when the code arrives. They save through their own APIs, live — exactly as on their
 * own screens; the wizard only hosts them.
 */
export function EditorSkeleton({ rows = 3, rowHeight = 68, toolbar = true }: { rows?: number; rowHeight?: number; toolbar?: boolean }) {
  return (
    <div aria-hidden="true" data-testid="editor-skeleton" className="space-y-2">
      {toolbar && <div className="flex justify-end"><div className={`${styles.skeleton} h-9 w-36`} /></div>}
      {Array.from({ length: rows }, (_, i) => <div key={i} className={`${styles.skeleton} w-full`} style={{ height: rowHeight }} />)}
    </div>
  );
}

export const EmbeddedLeaveTypes = dynamic(() => import('../../../views/LeavePage').then(m => m.LeaveTypesTab), { ssr: false, loading: () => <EditorSkeleton /> });
export const EmbeddedLeavePolicies = dynamic(() => import('../../../views/LeavePage').then(m => m.PoliciesTab), { ssr: false, loading: () => <EditorSkeleton /> });
export const EmbeddedHolidayCalendars = dynamic(() => import('../../../views/LeavePage').then(m => m.HolidayCalendarTab), { ssr: false, loading: () => <EditorSkeleton rows={2} /> });
export const EmbeddedCountryRules = dynamic(() => import('../../../views/SetupPage').then(m => m.GCCSettingsTab), { ssr: false, loading: () => <EditorSkeleton rows={1} rowHeight={148} toolbar={false} /> });
export const EmbeddedStatutoryRules = dynamic(() => import('../../StatutoryRulesPanel').then(m => m.StatutoryRulesPanel), { ssr: false, loading: () => <EditorSkeleton rows={4} rowHeight={56} /> });
