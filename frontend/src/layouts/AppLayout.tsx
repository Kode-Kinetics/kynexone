'use client';
import { SetupWorkspaceReturn } from '../components/setup/SetupWorkspaceReturn';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { MessageSquareText, Clock, Search, X } from 'lucide-react';
import { useRouter, usePathname } from 'next/navigation';
import { Sidebar } from './Sidebar';
import { TopBar } from './TopBar';
import { AssistantLauncher } from './AssistantLauncher';
import dynamic from 'next/dynamic';

// The drawer's code loads the first time someone opens it, not on every page load.
const AssistantDrawer = dynamic(() => import('./AssistantDrawer').then((m) => m.AssistantDrawer), { ssr: false });
import { MobileBottomNav } from './MobileBottomNav';
import { MfaEnrollmentPrompt } from '../components/MfaEnrollmentPrompt';
import { ResetCodeNotice } from '../components/ResetCodeNotice';
import { authApi } from '../api/auth';
import { employeesApi } from '../api/employees';
import { reportsApi } from '../api/reports';
import { usersApi } from '../api/identity';
import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { commandNavigationItems } from '../routes/navigation';
import type { ThemeMode } from '../types/ui';
import { useNavigationGuard } from '../contexts/NavigationGuardContext';
import { employeeWorkspaceHref } from '../lib/peopleRoutes';
import { POLICY_INTAKE_FLAG } from '../lib/policyIntake';
import { permissionAccessDescription } from '../lib/permissionAreas';
import { useLocale } from '../contexts/LocaleContext';
import { useCompany } from '../contexts/CompanyContext';
import { setupNavigationModel } from '../lib/setupAreas';
import { useCanOpenSetup } from '../hooks/useCanOpenSetup';
import { buildSetupSearchIndex, matchSetupEntries } from '../components/setup/setupSearch';

/** Setup's own route gate (app/(dashboard)/setup/page.tsx): any one of these opens /setup. */

const HISTORY_KEY = 'kynexone-search-history';
const MAX_HISTORY = 8;

function loadHistory(): string[] {
  if (typeof window === 'undefined') return [];
  try { return JSON.parse(localStorage.getItem(HISTORY_KEY) ?? '[]'); } catch { return []; }
}

function saveToHistory(query: string) {
  const trimmed = query.trim();
  if (!trimmed || trimmed.length < 2) return;
  const existing = loadHistory().filter((h) => h.toLowerCase() !== trimmed.toLowerCase());
  const next = [trimmed, ...existing].slice(0, MAX_HISTORY);
  localStorage.setItem(HISTORY_KEY, JSON.stringify(next));
}

interface AppLayoutProps {
  children: React.ReactNode;
  theme: ThemeMode;
  onToggleTheme: () => void;
}

interface PaletteItem {
  id: string;
  label: string;
  sublabel: string;
  icon: 'assistant' | 'search';
  onSelect: () => void;
}

export function AppLayout({ children, theme, onToggleTheme }: AppLayoutProps) {
  const router = useRouter();
  const navigationGuard = useNavigationGuard();
  const pathname = usePathname();
  // <main> remounts (and fades in) on navigation. The Self-Service workspace's tabs are one screen:
  // keyed as one, its tab row stays put and keeps keyboard focus when a tab is chosen.
  const mainKey = pathname === '/ess' || pathname?.startsWith('/ess/') ? '/ess' : pathname;
  const { hasPermission, hasRole, beginMfaEnrollment } = useAuth();
  const { accountType, isGroupScope } = useCompany();
  const startMfaEnrollment = useCallback(async () => {
    await beginMfaEnrollment();
    router.replace('/login');
  }, [beginMfaEnrollment, router]);
  const featureFlags = useFeatureFlags();
  const canOpenSetup = useCanOpenSetup();
  const { isFeatureEnabled } = featureFlags;
  const { t, locale } = useLocale();
  // ess.read alone opens the assistant only for policy questions, which exist only while policy intake is on.
  const mayUseAssistant = isFeatureEnabled('ai_assistant') && (hasPermission('ai.query') || hasPermission('ai.insights_view')
    || (isFeatureEnabled(POLICY_INTAKE_FLAG) && hasPermission('ess.read')));
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => typeof window !== 'undefined' && localStorage.getItem('sidebar-collapsed') === 'true');
  const [commandOpen, setCommandOpen] = useState(false);
  // Lives here, not in a page: <main> is keyed by pathname and remounts on navigation.
  const [assistantOpen, setAssistantOpen] = useState(false);
  const [assistantUsed, setAssistantUsed] = useState(false);
  const [commandQuery, setCommandQuery] = useState('');
  const [searchHistory, setSearchHistory] = useState<string[]>([]);
  const [employeeResults, setEmployeeResults] = useState<Array<{ id: number; label: string; sublabel: string }>>([]);
  const [userResults, setUserResults] = useState<Array<{ id: string; label: string; sublabel: string; searchText: string }>>([]);
  const [reportResults, setReportResults] = useState<Array<{ key: string; label: string; sublabel: string }>>([]);
  const [searching, setSearching] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);
  const commandTriggerRef = useRef<HTMLButtonElement>(null);
  const sidebarTriggerRef = useRef<HTMLButtonElement>(null);

  const commandItems = useMemo(() => {
    const base = commandNavigationItems
      .filter((item): item is typeof item & { path: string } => Boolean(item.path))
      .map((item) => ({
        label: t(item.label),
        // The English name stays searchable in every language: people type "payroll" in Arabic too.
        english: item.label,
        path: item.path,
        // In words ("Needs payroll access"), never the raw permission key.
        description: permissionAccessDescription(item.requiredPermissions ?? [], locale, t),
      }));

    const extras = [
      { label: t('Kody'), english: 'Kody', path: '/ai-assistant', description: t('Open Kody the HR Assistant') },
      { label: t('Search Employees'), english: 'Search Employees', path: '/people', description: t('Jump to employee search and records') },
      { label: t('Review Approvals'), english: 'Review Approvals', path: '/approvals', description: t('Open approval center') },
      { label: t('View Reports'), english: 'View Reports', path: '/reports', description: t('Open reports and analytics') },
    ];

    return [...new Map([...extras, ...base].map((item) => [item.path, item])).values()];
  }, [locale, t]);

  const visibleModules = useMemo(
    () => commandItems.filter((item) => {
      if (item.path === '/ai-assistant') return mayUseAssistant;
      const navMatch = commandNavigationItems.find((nav) => nav.path === item.path);
      // A feature-flagged screen (e.g. Release A, off unless enabled) is offered only when its flag is on.
      if (navMatch?.requiredFeatureKey && !isFeatureEnabled(navMatch.requiredFeatureKey)) return false;
      if (navMatch?.requiresSetupSection && !canOpenSetup) return false;
      if (!navMatch?.requiredPermissions?.length) return true;
      // Any one of them, as the sidebar, ProtectedRoute and the page gates read the same list.
      return navMatch.requiredPermissions.some((permission) => hasPermission(permission));
    }),
    [commandItems, hasPermission, mayUseAssistant, isFeatureEnabled, canOpenSetup],
  );

  const filteredCommands = useMemo(() => {
    const term = commandQuery.trim().toLowerCase();
    if (!term) return visibleModules.slice(0, 10);
    return visibleModules
      .filter((item) =>
        item.label.toLowerCase().includes(term) ||
        item.english.toLowerCase().includes(term) ||
        item.path.toLowerCase().includes(term) ||
        item.description.toLowerCase().includes(term)
      )
      .slice(0, 10);
  }, [commandQuery, visibleModules]);

  // Settings: Setup's own "Find a setting" entries, from the same model as Setup's navigation, so the palette never
  // offers a section the navigation would hide from this user.
  const setupEntries = useMemo(() => {
    if (!canOpenSetup || !featureFlags.verdictForPath('/setup').allowed) return [];
    const isCompanyScopedGroup = accountType === 'Group' && !isGroupScope;
    const model = setupNavigationModel({ hasPermission, hasRole, isCompanyScopedGroup, policyIntake: isFeatureEnabled(POLICY_INTAKE_FLAG), isFeatureEnabled, verdictForPath: featureFlags.verdictForPath, flagStatus: featureFlags.status });
    return buildSetupSearchIndex({
      model,
      access: { hasPermission, hasRole, isFeatureEnabled, verdictForPath: featureFlags.verdictForPath, flagStatus: featureFlags.status, isCompanyScopedGroup },
    });
  }, [accountType, canOpenSetup, featureFlags.status, featureFlags.verdictForPath, hasPermission, hasRole, isFeatureEnabled, isGroupScope]);
  const settingResults = useMemo(
    () => (commandQuery.trim() ? matchSetupEntries(setupEntries, commandQuery, t).slice(0, 6) : []),
    [commandQuery, setupEntries, t],
  );

  const showingSearchResults = commandQuery.trim().length >= 2
    && (employeeResults.length > 0 || userResults.length > 0 || reportResults.length > 0);

  useEffect(() => {
    if (!commandOpen) return;
    const term = commandQuery.trim();
    if (term.length < 2) {
      setEmployeeResults([]);
      setUserResults([]);
      setReportResults([]);
      setSearching(false);
      return;
    }

    let cancelled = false;
    setSearching(true);
    const timer = window.setTimeout(async () => {
      try {
        const tasks: Promise<void>[] = [];

        if (hasPermission('employees.read')) {
          tasks.push(
            employeesApi.list({ search: term, page: 1, pageSize: 5 })
              .then((result) => {
                if (cancelled) return;
                setEmployeeResults(
                  result.items.map((employee) => ({
                    id: employee.id,
                    label: `${employee.fullName} · ${employee.employeeCode}`,
                    sublabel: `${employee.department || 'No department'} · ${employee.designation || 'No title'}`,
                  })),
                );
              })
              .catch(() => {
                if (!cancelled) setEmployeeResults([]);
              }),
          );
        } else {
          setEmployeeResults([]);
        }

        if (hasPermission('users.manage') || hasPermission('security.manage')) {
          tasks.push(
            usersApi.list({ search: term, page: 1, pageSize: 5 })
              .then((result) => {
                if (cancelled) return;
                setUserResults(
                  result.items.map((user) => ({
                    id: user.id,
                    label: `${user.fullName} · ${user.email}`,
                    sublabel: `${user.status} · ${user.roles.join(', ') || 'No roles'}`,
                    searchText: user.email || user.fullName,
                  })),
                );
              })
              .catch(() => {
                if (!cancelled) setUserResults([]);
              }),
          );
        } else {
          setUserResults([]);
        }

        if (hasPermission('reports.read')) {
          tasks.push(
            reportsApi.catalog()
              .then((catalog) => {
                if (cancelled) return;
                const matches = catalog
                  .filter((report) =>
                    report.name.toLowerCase().includes(term.toLowerCase()) ||
                    report.key.toLowerCase().includes(term.toLowerCase()) ||
                    report.description.toLowerCase().includes(term.toLowerCase()) ||
                    report.category.toLowerCase().includes(term.toLowerCase()),
                  )
                  .slice(0, 5)
                  .map((report) => ({
                    key: report.key,
                    label: report.name,
                    sublabel: `${report.category} · ${report.description}`,
                  }));
                setReportResults(matches);
              })
              .catch(() => {
                if (!cancelled) setReportResults([]);
              }),
          );
        } else {
          setReportResults([]);
        }

        await Promise.allSettled(tasks);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 220);

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [commandOpen, commandQuery, hasPermission]);

  useEffect(() => {
    if (commandOpen) setActiveIndex(0);
  }, [commandOpen, commandQuery]);

  const openCommandPalette = () => {
    setSearchHistory(loadHistory());
    setCommandQuery('');
    setCommandOpen(true);
  };

  const closeCommandPalette = () => setCommandOpen(false);

  const runCommand = (path: string) => {
    if (commandQuery.trim().length >= 2) saveToHistory(commandQuery.trim());
    setSearchHistory(loadHistory());
    navigationGuard.push(path, commandTriggerRef.current);
    closeCommandPalette();
  };

  const openEmployeeResult = (id: number) => {
    const path = employeeWorkspaceHref(id, { section: 'overview' });
    if (commandQuery.trim().length >= 2) saveToHistory(commandQuery.trim());
    setSearchHistory(loadHistory());
    navigationGuard.push(path, commandTriggerRef.current);
    closeCommandPalette();
  };

  const openUserResult = (searchText: string) => {
    const path = `/setup?tab=users&search=${encodeURIComponent(searchText)}`;
    if (commandQuery.trim().length >= 2) saveToHistory(commandQuery.trim());
    setSearchHistory(loadHistory());
    navigationGuard.push(path, commandTriggerRef.current);
    closeCommandPalette();
  };

  const openReportResult = (key: string) => {
    const path = `/reports?report=${encodeURIComponent(key)}`;
    if (commandQuery.trim().length >= 2) saveToHistory(commandQuery.trim());
    setSearchHistory(loadHistory());
    navigationGuard.push(path, commandTriggerRef.current);
    closeCommandPalette();
  };

  const openSettingResult = (href: string) => {
    if (commandQuery.trim().length >= 2) saveToHistory(commandQuery.trim());
    setSearchHistory(loadHistory());
    navigationGuard.push(href, commandTriggerRef.current);
    closeCommandPalette();
  };

  const removeHistoryItem = (item: string) => {
    const next = searchHistory.filter((h) => h !== item);
    localStorage.setItem(HISTORY_KEY, JSON.stringify(next));
    setSearchHistory(next);
  };

  const clearHistory = () => {
    localStorage.removeItem(HISTORY_KEY);
    setSearchHistory([]);
  };

  const paletteItems = useMemo<PaletteItem[]>(() => {
    // Shown after the other results, in the same order as on screen.
    const settings = settingResults.map((hit) => ({
      id: `setting-${hit.entry.id}`,
      label: hit.label,
      sublabel: hit.group,
      icon: 'search' as const,
      onSelect: () => openSettingResult(hit.entry.href),
    }));
    if (showingSearchResults) {
      return [
        ...employeeResults.map((item) => ({
          id: `employee-${item.id}`,
          label: item.label,
          sublabel: item.sublabel,
          icon: 'search' as const,
          onSelect: () => openEmployeeResult(item.id),
        })),
        ...userResults.map((item) => ({
          id: `user-${item.id}`,
          label: item.label,
          sublabel: item.sublabel,
          icon: 'search' as const,
          onSelect: () => openUserResult(item.searchText),
        })),
        ...reportResults.map((item) => ({
          id: `report-${item.key}`,
          label: item.label,
          sublabel: item.sublabel,
          icon: 'search' as const,
          onSelect: () => openReportResult(item.key),
        })),
        ...settings,
      ];
    }

    return [...filteredCommands.map((item) => ({
      id: `${item.path}-${item.label}`,
      label: item.label,
      sublabel: item.description,
      icon: item.path === '/ai-assistant' ? 'assistant' as const : 'search' as const,
      onSelect: () => runCommand(item.path),
    })), ...settings];
  }, [employeeResults, filteredCommands, reportResults, settingResults, showingSearchResults, userResults]);

  useEffect(() => {
    if (!commandOpen) {
      setActiveIndex(0);
      return;
    }
    setActiveIndex((current) => (paletteItems.length === 0 ? 0 : Math.min(current, paletteItems.length - 1)));
  }, [commandOpen, paletteItems.length]);

  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      if (commandOpen) {
        if (event.key === 'ArrowDown') {
          event.preventDefault();
          setActiveIndex((current) => (paletteItems.length === 0 ? 0 : (current + 1) % paletteItems.length));
          return;
        }

        if (event.key === 'ArrowUp') {
          event.preventDefault();
          setActiveIndex((current) => (paletteItems.length === 0 ? 0 : (current - 1 + paletteItems.length) % paletteItems.length));
          return;
        }

        if (event.key === 'Enter') {
          event.preventDefault();
          paletteItems[activeIndex]?.onSelect();
          return;
        }
      }

      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        openCommandPalette();
        return;
      }

      if (event.key === 'Escape') {
        setCommandOpen(false);
      }
    };

    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [activeIndex, commandOpen, paletteItems]);

  // overflow-x-CLIP, not hidden: `hidden` turns this div into a scroll container, which
  // silently disabled every position:sticky inside it (the command bar scrolled away).
  return (
    <div className="tenant-app-shell wg-canvas min-h-screen overflow-x-clip text-slate-950 dark:text-white">
      <div className="flex min-h-screen">
        <Sidebar
          isOpen={sidebarOpen}
          isCollapsed={sidebarCollapsed}
          onClose={() => setSidebarOpen(false)}
          onToggleCollapse={() => setSidebarCollapsed((c) => {
            const next = !c;
            localStorage.setItem('sidebar-collapsed', String(next));
            return next;
          })}
          navigationRestoreTarget={sidebarTriggerRef}
        />
        <div className="min-w-0 flex-1">
          <TopBar
            theme={theme}
            onToggleTheme={onToggleTheme}
            onOpenSidebar={() => setSidebarOpen(true)}
            onOpenSearch={openCommandPalette}
            navigationButtonRef={sidebarTriggerRef}
            searchButtonRef={commandTriggerRef}
          />
          <MfaEnrollmentPrompt
            loadStatus={authApi.mfaStatus}
            onStart={startMfaEnrollment}
            dismissKey="kynexone-mfa-prompt-later"
            className="mx-4 mt-4 sm:mx-6 lg:mx-8"
          />
          <ResetCodeNotice className="mx-4 mt-4 sm:mx-6 lg:mx-8" />
          {/* Bottom padding below lg clears the fixed bottom nav and the device safe area. */}
          <main key={mainKey} className={`animate-fade-in-up px-4 pt-6 sm:px-6 lg:px-8 ${mayUseAssistant && pathname !== '/ai-assistant' ? 'pb-[calc(9rem+env(safe-area-inset-bottom))] lg:pb-24' : 'pb-[calc(5.5rem+env(safe-area-inset-bottom))] lg:pb-8'}`}><SetupWorkspaceReturn />{children}</main>
        </div>
      </div>

      <MobileBottomNav onOpenMore={() => setSidebarOpen(true)} />
      {mayUseAssistant && pathname !== '/ai-assistant' && (
        <AssistantLauncher open={assistantOpen} onOpen={() => { setAssistantUsed(true); setAssistantOpen(true); }} />
      )}
      {mayUseAssistant && assistantUsed && <AssistantDrawer open={assistantOpen} onClose={() => setAssistantOpen(false)} />}

      {commandOpen && (
        <div
          className="fixed inset-0 z-50 flex items-start justify-center bg-slate-950/50 px-4 pt-24 backdrop-blur-sm"
          onMouseDown={(event) => {
            if (event.target === event.currentTarget) closeCommandPalette();
          }}
        >
          <div className="w-full max-w-2xl overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-2xl dark:border-white/10 dark:bg-[#0D1221]">
            <div className="flex items-center gap-3 border-b border-slate-100 px-4 py-4 dark:border-white/[0.07]">
              <Search className="h-4 w-4 text-slate-400" />
              <input
                autoFocus
                value={commandQuery}
                onChange={(event) => setCommandQuery(event.target.value)}
                placeholder={t('Search modules, employees, reports…')}
                className="w-full bg-transparent text-sm text-slate-900 outline-none placeholder:text-slate-400 dark:text-white dark:placeholder:text-slate-500"
              />
              <button
                type="button"
                aria-label={t('Close search')}
                onClick={closeCommandPalette}
                className="grid h-7 w-7 place-items-center rounded-md text-slate-400 hover:bg-slate-100 dark:hover:bg-white/10"
              >
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="max-h-[26rem] overflow-y-auto p-2">
              {/* Recent searches — shown when query is empty */}
              {!commandQuery.trim() && searchHistory.length > 0 && (
                <div className="mb-2 p-2">
                  <div className="mb-1 flex items-center justify-between px-3">
                    <p className="text-[10px] font-bold uppercase tracking-wide text-slate-400 rtl:text-[13px] rtl:normal-case rtl:tracking-normal">{t('Recent searches')}</p>
                    <button type="button" onClick={clearHistory} className="text-[10px] text-slate-400 hover:text-rose-500 transition">{t('Clear all')}</button>
                  </div>
                  <div className="space-y-0.5">
                    {searchHistory.map((item) => (
                      <div key={item} className="group flex items-center gap-2 rounded-xl px-4 py-2.5 hover:bg-slate-50 dark:hover:bg-white/[0.05]">
                        <Clock className="h-3.5 w-3.5 shrink-0 text-slate-300 dark:text-slate-600" />
                        <button type="button" className="flex-1 text-start text-sm text-slate-600 dark:text-slate-300" onClick={() => setCommandQuery(item)}>
                          {item}
                        </button>
                        <button type="button" onClick={() => removeHistoryItem(item)} title={t('Remove from history')} className="opacity-0 group-hover:opacity-100 transition grid h-5 w-5 place-items-center rounded text-slate-300 hover:text-slate-500 dark:text-slate-600 dark:hover:text-slate-400">
                          <X className="h-3 w-3" />
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
              )}
              {showingSearchResults ? (
                <div className="space-y-3 p-2">
                  {employeeResults.length > 0 && (
                    <div>
                      <p className="px-3 pb-1 text-[10px] font-bold uppercase tracking-wide text-slate-400 rtl:text-[13px] rtl:normal-case rtl:tracking-normal">{t('Employees')}</p>
                      <div className="space-y-1">
                        {employeeResults.map((item) => (
                          (() => {
                            const index = paletteItems.findIndex((entry) => entry.id === `employee-${item.id}`);
                            const selected = index === activeIndex;
                            return (
                          <button
                            key={`employee-${item.id}`}
                            type="button"
                            onClick={() => openEmployeeResult(item.id)}
                            className={`flex w-full items-center gap-3 rounded-xl px-4 py-3 text-start transition ${selected ? 'bg-sapphire/10 dark:bg-cyanAccent/10' : 'hover:bg-slate-50 dark:hover:bg-white/[0.05]'}`}
                          >
                            <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-sapphire/10 text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent">
                              <Search className="h-4 w-4" />
                            </span>
                            <span className="min-w-0 flex-1">
                              <span className="block truncate text-sm font-semibold text-slate-900 dark:text-white">{item.label}</span>
                              <span className="block truncate text-xs text-slate-500 dark:text-slate-400">{item.sublabel}</span>
                            </span>
                          </button>
                            );
                          })()
                        ))}
                      </div>
                    </div>
                  )}

                  {userResults.length > 0 && (
                    <div>
                      <p className="px-3 pb-1 text-[10px] font-bold uppercase tracking-wide text-slate-400 rtl:text-[13px] rtl:normal-case rtl:tracking-normal">{t('Users')}</p>
                      <div className="space-y-1">
                        {userResults.map((item) => (
                          (() => {
                            const index = paletteItems.findIndex((entry) => entry.id === `user-${item.id}`);
                            const selected = index === activeIndex;
                            return (
                          <button
                            key={`user-${item.id}`}
                            type="button"
                            onClick={() => openUserResult(item.searchText)}
                            className={`flex w-full items-center gap-3 rounded-xl px-4 py-3 text-start transition ${selected ? 'bg-sapphire/10 dark:bg-cyanAccent/10' : 'hover:bg-slate-50 dark:hover:bg-white/[0.05]'}`}
                          >
                            <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-100 text-slate-500 dark:bg-white/[0.06] dark:text-slate-300">
                              <Search className="h-4 w-4" />
                            </span>
                            <span className="min-w-0 flex-1">
                              <span className="block truncate text-sm font-semibold text-slate-900 dark:text-white">{item.label}</span>
                              <span className="block truncate text-xs text-slate-500 dark:text-slate-400">{item.sublabel}</span>
                            </span>
                          </button>
                            );
                          })()
                        ))}
                      </div>
                    </div>
                  )}

                  {reportResults.length > 0 && (
                    <div>
                      <p className="px-3 pb-1 text-[10px] font-bold uppercase tracking-wide text-slate-400 rtl:text-[13px] rtl:normal-case rtl:tracking-normal">{t('Reports')}</p>
                      <div className="space-y-1">
                        {reportResults.map((item) => (
                          (() => {
                            const index = paletteItems.findIndex((entry) => entry.id === `report-${item.key}`);
                            const selected = index === activeIndex;
                            return (
                          <button
                            key={`report-${item.key}`}
                            type="button"
                            onClick={() => openReportResult(item.key)}
                            className={`flex w-full items-center gap-3 rounded-xl px-4 py-3 text-start transition ${selected ? 'bg-sapphire/10 dark:bg-cyanAccent/10' : 'hover:bg-slate-50 dark:hover:bg-white/[0.05]'}`}
                          >
                            <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-100 text-slate-500 dark:bg-white/[0.06] dark:text-slate-300">
                              <Search className="h-4 w-4" />
                            </span>
                            <span className="min-w-0 flex-1">
                              <span className="block truncate text-sm font-semibold text-slate-900 dark:text-white">{item.label}</span>
                              <span className="block truncate text-xs text-slate-500 dark:text-slate-400">{item.sublabel}</span>
                            </span>
                          </button>
                            );
                          })()
                        ))}
                      </div>
                    </div>
                  )}

                  {searching && (
                    <div className="px-4 py-3 text-sm text-slate-500 dark:text-slate-400">{t('Searching across employees, users, and reports…')}</div>
                  )}
                </div>
              ) : filteredCommands.length === 0 && settingResults.length === 0 ? (
                <div className="px-4 py-10 text-center text-sm text-slate-500 dark:text-slate-400">
                  {t('No matches found. Try a module name like payroll, leave, or reports.')}
                </div>
              ) : (
                filteredCommands.map((item) => {
                  const isAssistant = item.path === '/ai-assistant';
                  const index = paletteItems.findIndex((entry) => entry.id === `${item.path}-${item.label}`);
                  const selected = index === activeIndex;
                  return (
                    <button
                      key={`${item.path}-${item.label}`}
                      type="button"
                      onClick={() => runCommand(item.path)}
                      className={`flex w-full items-center gap-3 rounded-xl px-4 py-3 text-start transition ${selected ? 'bg-sapphire/10 dark:bg-cyanAccent/10' : 'hover:bg-slate-50 dark:hover:bg-white/[0.05]'}`}
                    >
                      <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-100 text-slate-500 dark:bg-white/[0.06] dark:text-slate-300">
                        {isAssistant ? <MessageSquareText className="h-4 w-4" /> : <Search className="h-4 w-4" />}
                      </span>
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-sm font-semibold text-slate-900 dark:text-white">{item.label}</span>
                        <span className="block truncate text-xs text-slate-500 dark:text-slate-400">{item.description}</span>
                      </span>
                      <span className="rounded-full border border-slate-200 px-2 py-0.5 text-[10px] font-medium text-slate-400 dark:border-white/10 dark:text-slate-500">
                        {item.path}
                      </span>
                    </button>
                  );
                })
              )}
              {settingResults.length > 0 && (
                <div className="p-2" data-testid="command-palette-settings">
                  <p id="command-palette-settings-label" className="px-3 pb-1 text-[10px] font-bold uppercase tracking-wide text-slate-400 rtl:text-[13px] rtl:normal-case rtl:tracking-normal">{t('Settings')}</p>
                  <div role="group" aria-labelledby="command-palette-settings-label" className="space-y-1">
                    {settingResults.map((hit) => {
                      const index = paletteItems.findIndex((entry) => entry.id === `setting-${hit.entry.id}`);
                      const selected = index === activeIndex;
                      return (
                        <button
                          key={`setting-${hit.entry.id}`}
                          type="button"
                          onClick={() => openSettingResult(hit.entry.href)}
                          className={`flex w-full items-center gap-3 rounded-xl px-4 py-3 text-start transition ${selected ? 'bg-sapphire/10 dark:bg-cyanAccent/10' : 'hover:bg-slate-50 dark:hover:bg-white/[0.05]'}`}
                        >
                          <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-100 text-slate-500 dark:bg-white/[0.06] dark:text-slate-300">
                            <Search className="h-4 w-4" />
                          </span>
                          <span className="min-w-0 flex-1">
                            <span className="block truncate text-sm font-semibold text-slate-900 dark:text-white"><bdi>{hit.label}</bdi></span>
                            <span className="block truncate text-xs text-slate-500 dark:text-slate-400">{hit.group}</span>
                          </span>
                        </button>
                      );
                    })}
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
