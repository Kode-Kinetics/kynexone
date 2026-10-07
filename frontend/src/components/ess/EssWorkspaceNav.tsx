'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useAuth } from '@/src/contexts/AuthContext';
import { useFeatureFlags } from '@/src/contexts/FeatureFlagContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { ESS_SECTIONS, activeEssPage, activeEssSection, visibleEssPages } from '@/src/routes/essSections';

/**
 * The Self-Service workspace's own navigation: one row of sections (Overview, Pay, Leave and time,
 * Requests, Benefits) and, under a section with more than one page, a row of its tabs. Both rows
 * are plain links, so every page keeps its own URL, back button and deep link.
 *
 * <main> remounts on every navigation (AppLayout keys it by pathname), so this reads nothing from
 * the server: the sections come from routes/essSections.ts, the countries from the login.
 */
export function EssWorkspaceNav() {
  const pathname = usePathname() ?? '';
  const { user, hasPermission } = useAuth();
  const { isFeatureEnabled, verdictForPath } = useFeatureFlags();
  const { t } = useLocale();

  // Without ess.read the page's own gate explains the refusal; a menu to pages it refuses would not.
  if (!hasPermission('ess.read')) return null;

  const countryCodes = (user?.companies ?? []).map((c) => c.countryCode);
  const sections = ESS_SECTIONS
    // As the sidebar does: a page whose module is switched off is not offered (ModuleGate would refuse it).
    .map((s) => ({ section: s, pages: visibleEssPages(s, isFeatureEnabled, countryCodes).filter((p) => verdictForPath(p.path).allowed) }))
    .filter((s) => s.pages.length > 0);
  const current = activeEssSection(pathname);
  const currentPage = activeEssPage(pathname);
  const tabs = sections.find((s) => s.section.id === current?.id)?.pages ?? [];

  return (
    <div className="wg-card overflow-hidden rounded-2xl" data-testid="ess-workspace-nav">
      <nav aria-label={t('Self-service sections')} className="overflow-x-auto border-b border-slate-100 px-2 dark:border-white/[0.07] sm:px-3">
        {/* Phones: the five sections share the width (icon over label), so none hides off the edge. */}
        <ul className="grid auto-cols-fr grid-flow-col gap-1 sm:flex sm:min-w-max">
          {sections.map(({ section, pages }) => {
            const active = section.id === current?.id;
            const Icon = section.icon;
            return (
              <li key={section.id}>
                <Link
                  href={pages[0].path}
                  // 'page' only when this link IS the page; on another tab of its section it marks the section.
                  aria-current={active ? (pages[0].path === pathname ? 'page' : 'true') : undefined}
                  title={t(pages[0].hint)}
                  className={`relative flex flex-col items-center gap-1 rounded-t-lg px-1 pb-2 pt-3 text-center text-[11px] font-semibold leading-tight outline-none transition-colors focus-visible:ring-2 focus-visible:ring-sapphire sm:inline-flex sm:flex-row sm:gap-2 sm:px-3 sm:pb-2.5 sm:pt-3.5 sm:text-sm ${
                    active
                      ? 'text-sapphire dark:text-cyanAccent'
                      : 'text-slate-500 hover:bg-slate-50 hover:text-slate-800 dark:text-slate-400 dark:hover:bg-white/[0.04] dark:hover:text-slate-100'
                  }`}
                >
                  <Icon className="h-4 w-4" aria-hidden="true" />
                  {t(section.label)}
                  {active && <span className="absolute inset-x-2 bottom-0 h-0.5 rounded-full bg-sapphire dark:bg-cyanAccent" aria-hidden="true" />}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>

      {tabs.length > 1 ? (
        <nav aria-label={t('Pages in {section}', { section: t(current!.label) })} className="overflow-x-auto bg-slate-50/70 px-3 py-2 dark:bg-white/[0.02] sm:px-4">
          <ul className="flex min-w-max gap-1.5">
            {tabs.map((p) => {
              const active = p.path === currentPage?.path;
              return (
                <li key={p.path}>
                  <Link
                    href={p.path}
                    aria-current={active ? 'page' : undefined}
                    title={t(p.hint)}
                    className={`inline-flex items-center rounded-lg px-3 py-1.5 text-xs font-semibold outline-none transition-colors focus-visible:ring-2 focus-visible:ring-sapphire ${
                      active
                        ? 'bg-white text-slate-900 shadow-sm ring-1 ring-slate-200 dark:bg-white/[0.08] dark:text-white dark:ring-white/[0.1]'
                        : 'text-slate-500 hover:bg-white hover:text-slate-800 dark:text-slate-400 dark:hover:bg-white/[0.05] dark:hover:text-slate-100'
                    }`}
                  >
                    {t(p.tab)}
                  </Link>
                </li>
              );
            })}
          </ul>
        </nav>
      ) : null}
    </div>
  );
}
