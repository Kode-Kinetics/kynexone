'use client';

import { usePathname } from 'next/navigation';
import { PackageX } from 'lucide-react';
import Link from 'next/link';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useAuth } from '../contexts/AuthContext';

/**
 * Stops a switched-off module's pages from rendering.
 *
 * <p>Before this existed, switching a module off hid its navigation entry and nothing else. Typing
 * the URL, following a bookmark or clicking a deep link still rendered the full page shell; its
 * data calls then returned 403, which the API client deliberately silences for
 * `feature_not_enabled`. The result was a working-looking screen that stayed permanently empty
 * with no explanation — worse than either showing the module or saying it is off.</p>
 *
 * <p>Path ownership is resolved from the backend module catalog (served by
 * `GET /api/features/modules`), so this cannot drift from what the API actually enforces.</p>
 */
export function ModuleGate({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const { verdictForPath, isLoading } = useFeatureFlags();
  // Setup → Modules needs security.manage (TenantModulesController); nobody else is offered a link that refuses them.
  const { hasPermission } = useAuth();
  const canManageModules = hasPermission('security.manage');

  // Hold the page until module state is known.
  //
  // Rendering the children optimistically and gating a moment later was visibly wrong when driven
  // in a browser: the real page painted, fired its data calls, took six 403s that the API client
  // deliberately silences, and only then flipped to the disabled notice. Waiting costs one spinner
  // on first load — the module state is fetched once per session, not per navigation — and removes
  // both the flash and the pointless requests.
  if (isLoading) {
    return (
      <div className="flex min-h-[60vh] items-center justify-center" aria-busy="true">
        <div className="h-8 w-8 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
        <span className="sr-only">Loading</span>
      </div>
    );
  }

  const verdict = verdictForPath(pathname ?? '/');
  if (verdict.allowed) return <>{children}</>;

  return (
    <div
      className="flex min-h-[60vh] flex-col items-center justify-center px-4 text-center"
      data-testid="module-disabled"
      data-module={verdict.moduleKey}
    >
      <div className="flex h-14 w-14 items-center justify-center rounded-full bg-slate-100 dark:bg-slate-800">
        <PackageX className="h-7 w-7 text-slate-400 dark:text-slate-500" aria-hidden="true" />
      </div>

      <h1 className="mt-5 text-lg font-semibold text-slate-900 dark:text-slate-100">
        {verdict.moduleLabel ?? 'This module'} is switched off
      </h1>

      <p className="mt-2 max-w-md text-sm text-slate-500 dark:text-slate-400">
        Your organisation has turned this module off, so its pages and data are not available.
        An administrator can switch it back on at any time.
      </p>

      <div className="mt-6 flex flex-wrap items-center justify-center gap-3">
        <Link
          href="/dashboard"
          className="rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-sapphire/90 focus:outline-none focus-visible:ring-2 focus-visible:ring-sapphire focus-visible:ring-offset-2 dark:ring-offset-midnight"
        >
          Back to dashboard
        </Link>
        {canManageModules && <Link
          href="/setup?tab=modules"
          className="rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-50 focus:outline-none focus-visible:ring-2 focus-visible:ring-sapphire focus-visible:ring-offset-2 dark:border-slate-600 dark:text-slate-200 dark:hover:bg-slate-800 dark:ring-offset-midnight"
        >
          Manage modules
        </Link>}
      </div>
    </div>
  );
}
