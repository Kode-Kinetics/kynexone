'use client';

import { useEffect, useMemo, useState } from 'react';
import { MessageSquareText, Clock, Search, X } from 'lucide-react';
import { useRouter, usePathname } from 'next/navigation';
import { Sidebar } from './Sidebar';
import { TopBar } from './TopBar';
import { employeesApi } from '../api/employees';
import { reportsApi } from '../api/reports';
import { usersApi } from '../api/identity';
import { useAuth } from '../contexts/AuthContext';
import { LocaleProvider } from '../contexts/LocaleContext';
import { navigationItems } from '../routes/navigation';
import type { ThemeMode } from '../types/ui';

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
  const pathname = usePathname();
  const { hasPermission } = useAuth();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => typeof window !== 'undefined' && localStorage.getItem('sidebar-collapsed') === 'true');
  const [commandOpen, setCommandOpen] = useState(false);
  const [commandQuery, setCommandQuery] = useState('');
  const [searchHistory, setSearchHistory] = useState<string[]>([]);
  const [employeeResults, setEmployeeResults] = useState<Array<{ id: number; label: string; sublabel: string }>>([]);
  const [userResults, setUserResults] = useState<Array<{ id: string; label: string; sublabel: string; searchText: string }>>([]);
  const [reportResults, setReportResults] = useState<Array<{ key: string; label: string; sublabel: string }>>([]);
  const [searching, setSearching] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);

  const commandItems = useMemo(() => {
    const base = navigationItems
      .filter((item): item is typeof item & { path: string } => Boolean(item.path))
      .map((item) => ({
        label: item.label,
        path: item.path,
        description: item.requiredPermissions?.length
          ? `Requires ${item.requiredPermissions.join(' or ')}`
          : 'Open module',
      }));

    const extras = [
      { label: 'Assistant', path: '/ai-assistant', description: 'Open the workspace assistant' },
      { label: 'Search Employees', path: '/people', description: 'Jump to employee search and records' },
      { label: 'Review Approvals', path: '/approvals', description: 'Open approval center' },
      { label: 'View Reports', path: '/reports', description: 'Open reports and analytics' },
    ];

    return [...new Map([...extras, ...base].map((item) => [item.path, item])).values()];
  }, []);

  const visibleModules = useMemo(
    () => commandItems.filter((item) => {
      const navMatch = navigationItems.find((nav) => nav.path === item.path);
      if (!navMatch?.requiredPermissions?.length) return true;
      return navMatch.requiredPermissions.every((permission) => hasPermission(permission));
    }),
    [commandItems, hasPermission],
  );

  const filteredCommands = useMemo(() => {
    const term = commandQuery.trim().toLowerCase();
    if (!term) return visibleModules.slice(0, 10);
    return visibleModules
      .filter((item) =>
        item.label.toLowerCase().includes(term) ||
        item.path.toLowerCase().includes(term) ||
        item.description.toLowerCase().includes(term)
      )
      .slice(0, 10);
  }, [commandQuery, visibleModules]);

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
    router.push(path);
    closeCommandPalette();
  };

  const openEmployeeResult = (id: number) => {
    if (commandQuery.trim().length >= 2) saveToHistory(commandQuery.trim());
    setSearchHistory(loadHistory());
    router.push(`/people?employeeId=${id}`);
    closeCommandPalette();
  };

  const openUserResult = (searchText: string) => {
    if (commandQuery.trim().length >= 2) saveToHistory(commandQuery.trim());
    setSearchHistory(loadHistory());
    router.push(`/user-management?search=${encodeURIComponent(searchText)}`);
    closeCommandPalette();
  };

  const openReportResult = (key: string) => {
    if (commandQuery.trim().length >= 2) saveToHistory(commandQuery.trim());
    setSearchHistory(loadHistory());
    router.push(`/reports?report=${encodeURIComponent(key)}`);
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
      ];
    }

    return filteredCommands.map((item) => ({
      id: `${item.path}-${item.label}`,
      label: item.label,
      sublabel: item.description,
      icon: item.path === '/ai-assistant' ? 'assistant' as const : 'search' as const,
      onSelect: () => runCommand(item.path),
    }));
  }, [employeeResults, filteredCommands, reportResults, showingSearchResults, userResults]);

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

  return (
    <LocaleProvider>
    <div className="min-h-screen overflow-x-hidden bg-lightBg text-slate-950 dark:bg-midnight dark:text-white">
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
        />
        <div className="min-w-0 flex-1">
          <TopBar
            theme={theme}
            onToggleTheme={onToggleTheme}
            onOpenSidebar={() => setSidebarOpen(true)}
            onOpenSearch={openCommandPalette}
            onAskKynexOne={() => router.push('/ai-assistant')}
          />
          <main key={pathname} className="animate-fade-in-up px-4 py-6 sm:px-6 lg:px-8">{children}</main>
        </div>
      </div>

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
                placeholder="Search modules, employees, reports…"
                className="w-full bg-transparent text-sm text-slate-900 outline-none placeholder:text-slate-400 dark:text-white dark:placeholder:text-slate-500"
              />
              <button
                type="button"
                aria-label="Close search"
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
                    <p className="text-[10px] font-bold uppercase tracking-wide text-slate-400">Recent Searches</p>
                    <button type="button" onClick={clearHistory} className="text-[10px] text-slate-400 hover:text-rose-500 transition">Clear all</button>
                  </div>
                  <div className="space-y-0.5">
                    {searchHistory.map((item) => (
                      <div key={item} className="group flex items-center gap-2 rounded-xl px-4 py-2.5 hover:bg-slate-50 dark:hover:bg-white/[0.05]">
                        <Clock className="h-3.5 w-3.5 shrink-0 text-slate-300 dark:text-slate-600" />
                        <button type="button" className="flex-1 text-left text-sm text-slate-600 dark:text-slate-300" onClick={() => setCommandQuery(item)}>
                          {item}
                        </button>
                        <button type="button" onClick={() => removeHistoryItem(item)} title="Remove from history" className="opacity-0 group-hover:opacity-100 transition grid h-5 w-5 place-items-center rounded text-slate-300 hover:text-slate-500 dark:text-slate-600 dark:hover:text-slate-400">
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
                      <p className="px-3 pb-1 text-[10px] font-bold uppercase tracking-wide text-slate-400">Employees</p>
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
                            className={`flex w-full items-center gap-3 rounded-xl px-4 py-3 text-left transition ${selected ? 'bg-sapphire/10 dark:bg-cyanAccent/10' : 'hover:bg-slate-50 dark:hover:bg-white/[0.05]'}`}
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
                      <p className="px-3 pb-1 text-[10px] font-bold uppercase tracking-wide text-slate-400">Users</p>
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
                            className={`flex w-full items-center gap-3 rounded-xl px-4 py-3 text-left transition ${selected ? 'bg-sapphire/10 dark:bg-cyanAccent/10' : 'hover:bg-slate-50 dark:hover:bg-white/[0.05]'}`}
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
                      <p className="px-3 pb-1 text-[10px] font-bold uppercase tracking-wide text-slate-400">Reports</p>
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
                            className={`flex w-full items-center gap-3 rounded-xl px-4 py-3 text-left transition ${selected ? 'bg-sapphire/10 dark:bg-cyanAccent/10' : 'hover:bg-slate-50 dark:hover:bg-white/[0.05]'}`}
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
                    <div className="px-4 py-3 text-sm text-slate-500 dark:text-slate-400">Searching across employees, users, and reports…</div>
                  )}
                </div>
              ) : filteredCommands.length === 0 ? (
                <div className="px-4 py-10 text-center text-sm text-slate-500 dark:text-slate-400">
                  No matches found. Try a module name like payroll, leave, or reports.
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
                      className={`flex w-full items-center gap-3 rounded-xl px-4 py-3 text-left transition ${selected ? 'bg-sapphire/10 dark:bg-cyanAccent/10' : 'hover:bg-slate-50 dark:hover:bg-white/[0.05]'}`}
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
            </div>
          </div>
        </div>
      )}
    </div>
    </LocaleProvider>
  );
}
