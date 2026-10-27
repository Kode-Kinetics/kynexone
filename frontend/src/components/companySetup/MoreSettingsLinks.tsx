'use client';

import Link from 'next/link';
import { ArrowRight } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { msg } from '../../i18n/translations';
import { SETUP_AREAS, type SetupDestination } from '../../lib/setupAreas';

/**
 * A7: what the old setup studio could also configure, at the screen that owns it today. Each entry is an existing
 * Setup destination (same path, permission and scope as All setup areas), so the list never offers a screen the
 * viewer cannot open. What has no screen of its own (attendance rules, HR approval rules, editing an overtime policy)
 * stays in Setup → Policy starter, which is deliberately not linked from Guided setup.
 */
const MORE_SETTINGS: { id: string; what: string }[] = [
  { id: 'shifts', what: msg('Shift definitions and roster rules') },
  { id: 'overtime', what: msg('Overtime policies: add a policy and its rates') },
  { id: 'leave', what: msg('Leave types, extra leave policies and holiday calendars') },
  { id: 'benefits', what: msg('Benefit plans and who is eligible') },
  { id: 'gradeBenefits', what: msg('Allowances and benefits for each grade') },
  { id: 'payroll', what: msg('Salary structures and pay components') },
  { id: 'statutoryRules', what: msg('Statutory rates such as GOSI') },
];

const destinations = new Map<string, SetupDestination>(SETUP_AREAS.flatMap(area => area.destinations.map(d => [d.id, d] as const)));

/** Workspace localisation (language, time zone, working week) lives in Organisation Admin, outside the Setup areas. */
const LOCALISATION = { href: '/organisation-admin', permission: 'security.manage', label: msg('Language, time zone and working week'), what: msg('Workspace-wide display and calendar settings, in Organisation Admin') };

export function MoreSettingsLinks({ companyId }: { companyId?: string | null }) {
  const t = useT();
  const { hasPermission } = useAuth();
  const { accountType, isGroupScope } = useCompany();
  const companyScoped = accountType === 'Group' && !isGroupScope;
  const rows = MORE_SETTINGS.flatMap(({ id, what }) => {
    const d = destinations.get(id);
    if (!d) return [];
    if (d.requiredPermissions.length > 0 && !d.requiredPermissions.some(hasPermission)) return [];
    // A company super admin cannot change what every company shares.
    if (companyScoped && d.scope === 'tenant') return [];
    const href = companyId && d.path.startsWith('/setup') ? `${d.path}&companyId=${encodeURIComponent(companyId)}` : d.path;
    return [{ key: id, label: d.label, what, href }];
  });
  if (!companyScoped && hasPermission(LOCALISATION.permission)) rows.push({ key: 'localisation', label: LOCALISATION.label, what: LOCALISATION.what, href: LOCALISATION.href });
  if (rows.length === 0) return null;
  return (
    <div data-testid="more-settings">
      <ul className="divide-y divide-slate-100 rounded-xl border border-slate-200 bg-white dark:divide-white/10 dark:border-white/10 dark:bg-white/[0.02]">
        {rows.map(row => (
          <li key={row.key} className="flex flex-wrap items-center justify-between gap-3 px-4 py-2.5">
            <div className="min-w-0">
              <p className="text-sm font-medium text-slate-900 dark:text-white">{t(row.label)}</p>
              <p className="text-xs text-slate-600 dark:text-slate-400">{t(row.what)}</p>
            </div>
            <Link href={row.href} className="inline-flex items-center gap-1 text-sm font-semibold text-sapphire hover:underline dark:text-cyanAccent">
              {t('Open')}<span className="sr-only"> {t(row.label)}</span><ArrowRight className="h-3.5 w-3.5 rtl:rotate-180" aria-hidden="true" />
            </Link>
          </li>
        ))}
      </ul>
      <p className="mt-2 text-xs leading-5 text-slate-600 dark:text-slate-400">{t('Attendance rules and HR approval rules have no screen of their own yet. A group administrator prepares them in Setup → Policy starter.')}</p>
    </div>
  );
}
