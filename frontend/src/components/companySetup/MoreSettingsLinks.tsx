'use client';

import { useState, type MouseEvent } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { ArrowRight, ArrowUpRight, PanelRightOpen } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { useAuth } from '../../contexts/AuthContext';
import { msg } from '../../i18n/translations';
import { SETUP_AREAS, type SetupDestination } from '../../lib/setupAreas';
import { rememberCompanySetupReturn, withCompanySetupReturn, type CompanySetupReturn } from '../setup/companySetupReturn';
import { SettingsDrawer } from './rules/SettingsDrawer';
import { LeaveEditors } from './rules/LeaveEditors';
import { EmbeddedStatutoryRules } from './rules/EmbeddedEditors';
import { useDirtyReporter, useRulesAccess, type RulesPartId } from './rules/rulesShared';
import { SetupEditorState } from '../setup/SetupEditorState';

/**
 * A7 + B4: what the old setup studio could also configure, grouped by area. Every entry is an existing Setup
 * destination (same path, permission and scope as All setup areas), so the list never offers a screen the viewer
 * cannot open. Settings with an editor that fits beside the wizard open in place (a side panel, or the Leave &
 * holidays part); the rest open their own page, which then offers "Back to company setup" (setup/SetupWorkspaceReturn).
 * HR approval rules have no screen of their own yet and are set up by the group super admin.
 */
type Panel = 'leave' | 'statutory';
type Target = { kind: 'page'; href: string } | { kind: 'panel'; panel: Panel } | { kind: 'part'; part: RulesPartId };
interface Row { key: string; label: string; what: string; target: Target }

const GROUPS: { id: string; label: string; items: { id: string; /** The Setup destination it opens, when it is not `id`. */ dest?: string; label?: string; what: string }[] }[] = [
  { id: 'time', label: msg('Time & attendance'), items: [
    // Shift definitions and overtime policies are Setup sections now; Shifts and Overtime keep rosters and requests.
    { id: 'shifts', dest: 'shiftDefinitions', what: msg('Shift definitions and roster rules') },
    { id: 'attendanceRules', what: msg('Grace period, early leaving, half day and working day') },
    { id: 'overtime', dest: 'overtimePolicies', what: msg('Overtime policies: add a policy and its rates') },
  ] },
  { id: 'leave', label: msg('Leave & holidays'), items: [
    { id: 'leave', label: msg('Leave policies, holidays and leave types'), what: msg('Leave types, extra leave policies and holiday calendars') },
  ] },
  { id: 'pay', label: msg('Pay & benefits'), items: [
    { id: 'payroll', what: msg('Salary structures and pay components') },
    { id: 'benefits', what: msg('Benefit plans and who is eligible') },
    { id: 'gradeBenefits', what: msg('Allowances and benefits for each grade') },
  ] },
  { id: 'compliance', label: msg('Compliance'), items: [
    { id: 'statutoryRules', what: msg('Statutory rates such as GOSI') },
  ] },
];

const destinations = new Map<string, SetupDestination>(SETUP_AREAS.flatMap(area => area.destinations.map(d => [d.id, d] as const)));

/** Workspace localisation (language, time zone, working week) is its own Setup section, outside the Setup areas. */
const LOCALISATION = { href: '/setup?tab=localization', permission: 'security.manage', label: msg('Language, time zone and working week'), what: msg('Workspace-wide display and calendar settings, in Setup') };

const PANEL_COPY: Record<Panel, { title: string; description: string }> = {
  leave: { title: msg('Leave policies, holidays and leave types'), description: msg('This company’s own leave policies, holidays and leave types. Changes save as you edit.') },
  statutory: { title: msg('Statutory rules'), description: msg('Tenant-wide effective-dated rules used by payroll and compliance.') },
};

export function MoreSettingsLinks({ companyId, onOpenPart, returnTo, beforeLeave }: {
  companyId?: string | null;
  /** In the wizard: open a step-5 part instead of a side panel (Leave & holidays). */
  onOpenPart?: (part: RulesPartId) => void;
  /** In the wizard: page links carry the way back (`returnTo=companySetup&…`), so the page shows "Back to company setup". */
  returnTo?: CompanySetupReturn | null;
  /** In the wizard: saves the draft before a page link leaves it; false keeps the reader here. */
  beforeLeave?: () => Promise<boolean>;
}) {
  const t = useT();
  const router = useRouter();
  const [leaving, setLeaving] = useState('');
  const { hasPermission } = useAuth();
  const access = useRulesAccess();
  const [panel, setPanel] = useState<Panel>('leave');
  const [panelOpen, setPanelOpen] = useState(false);
  const reportStatutory = useDirtyReporter(msg('Statutory rules'));

  const allowed = (d: SetupDestination | undefined): d is SetupDestination => {
    if (!d) return false;
    if (d.requiredPermissions.length > 0 && !d.requiredPermissions.some(hasPermission)) return false;
    // A company super admin cannot change what every company shares.
    return !(access.companyScoped && d.scope === 'tenant');
  };
  const pageHref = (d: SetupDestination) => (companyId && d.path.startsWith('/setup') ? `${d.path}&companyId=${encodeURIComponent(companyId)}` : d.path);

  const groups = GROUPS.map(group => ({
    ...group,
    rows: group.items.flatMap<Row>(({ id, dest, label, what }) => {
      const d = destinations.get(dest ?? id);
      if (id === 'leave' && access.canEditLeave) return [{ key: id, label: label ?? d?.label ?? id, what, target: onOpenPart ? { kind: 'part', part: 'leave' } : { kind: 'panel', panel: 'leave' } }];
      if (id === 'statutoryRules' && access.canReadStatutoryRules) return [{ key: id, label: d?.label ?? id, what, target: { kind: 'panel', panel: 'statutory' } }];
      if (!allowed(d)) return [];
      return [{ key: id, label: label ?? d.label, what, target: { kind: 'page', href: pageHref(d) } }];
    }),
  }));
  if (!access.companyScoped && hasPermission(LOCALISATION.permission))
    groups.push({ id: 'workspace', label: msg('Workspace'), items: [], rows: [{ key: 'localisation', label: LOCALISATION.label, what: LOCALISATION.what, target: { kind: 'page', href: LOCALISATION.href } }] });
  const visible = groups.filter(g => g.rows.length > 0);
  if (visible.length === 0) return null;

  /** Same tab: save the draft first (as Save draft & exit does), then go. A new tab / window keeps the browser's own behaviour. */
  const leave = async (event: MouseEvent<HTMLAnchorElement>, href: string) => {
    if (returnTo) rememberCompanySetupReturn(returnTo);
    if (!beforeLeave || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || event.button !== 0) return;
    event.preventDefault();
    if (leaving) return;
    setLeaving(href);
    const saved = await beforeLeave().catch(() => false);
    setLeaving('');
    if (saved) router.push(href);
  };

  const rowClass = 'group flex w-full min-w-0 items-center gap-3 px-4 py-3 text-start transition-colors duration-150 hover:bg-slate-50 active:bg-slate-100 focus-visible:relative focus-visible:z-10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-sapphire dark:hover:bg-white/[0.04] dark:active:bg-white/[0.07] dark:focus-visible:ring-cyanAccent';
  // Arrow icons are mirrored for Arabic by the global stylesheet (index.css, `scale: -1 1`); the panel icon opts in
  // with `rtl-mirror`. No rtl: transform utilities here, which would flip them back.
  const hint = (target: Target, busy = false) => {
    const Icon = target.kind === 'page' ? ArrowUpRight : target.kind === 'part' ? ArrowRight : PanelRightOpen;
    return (
      <span className="ms-auto inline-flex shrink-0 items-center gap-1 text-xs font-medium text-slate-500 group-hover:text-sapphire dark:text-slate-400 dark:group-hover:text-cyanAccent">
        <span className="hidden min-[420px]:inline">{busy ? t('Saving the draft…') : target.kind === 'page' ? t('Opens its page') : t('Opens here')}</span>
        <Icon className={`h-4 w-4 ${target.kind === 'panel' ? 'rtl-mirror' : 'transition-transform duration-150 group-hover:translate-x-0.5 motion-reduce:transition-none motion-reduce:group-hover:translate-x-0'}`} aria-hidden="true" />
      </span>
    );
  };
  const body = (row: Row) => (
    <span className="min-w-0">
      <span className="block text-sm font-medium leading-5 text-slate-900 dark:text-white">{t(row.label)}</span>
      <span className="mt-0.5 block text-xs leading-5 text-slate-600 dark:text-slate-400">{t(row.what)}</span>
    </span>
  );

  return (
    <div data-testid="more-settings" className="space-y-5">
      {visible.map(group => (
        <section key={group.id} aria-labelledby={`more-settings-${group.id}`} data-testid={`more-settings-group-${group.id}`}>
          <h4 id={`more-settings-${group.id}`} className="mb-2 text-xs font-semibold tracking-wide text-slate-500 dark:text-slate-400">{t(group.label)}</h4>
          <ul className="divide-y divide-slate-100 overflow-hidden rounded-xl border border-slate-200 bg-white dark:divide-white/[0.06] dark:border-white/10 dark:bg-white/[0.02]">
            {group.rows.map(row => (
              <li key={row.key}>
                {row.target.kind === 'page'
                  ? (() => { const href = withCompanySetupReturn(row.target.href, returnTo ?? null); return (
                    <Link href={href} onClick={event => void leave(event, href)} aria-busy={leaving === href || undefined} className={rowClass} data-testid={`more-settings-${row.key}`} data-kind="page">{body(row)}{hint(row.target, leaving === href)}</Link>); })()
                  : <button type="button" className={rowClass} data-testid={`more-settings-${row.key}`} data-kind={row.target.kind} aria-haspopup={row.target.kind === 'panel' ? 'dialog' : undefined}
                      onClick={() => { const target = row.target; if (target.kind === 'part') onOpenPart?.(target.part); else if (target.kind === 'panel') { setPanel(target.panel); setPanelOpen(true); } }}>
                      {body(row)}{hint(row.target)}
                    </button>}
              </li>
            ))}
          </ul>
        </section>
      ))}
      <p className="text-xs leading-5 text-slate-600 dark:text-slate-400">{t('HR approval rules have no screen of their own yet. Ask your group super admin to set these up.')}</p>
      <SettingsDrawer open={panelOpen} wide={panel === 'statutory'} title={t(PANEL_COPY[panel].title)} description={t(PANEL_COPY[panel].description)} onClose={() => setPanelOpen(false)}>
        {panel === 'leave' && <LeaveEditors companyId={companyId ?? undefined} idPrefix="more-leave" />}
        {panel === 'statutory' && <SetupEditorState.Provider value={reportStatutory}><EmbeddedStatutoryRules /></SetupEditorState.Provider>}
      </SettingsDrawer>
    </div>
  );
}
