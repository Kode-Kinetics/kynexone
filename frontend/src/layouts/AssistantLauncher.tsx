'use client';

import { usePathname } from 'next/navigation';
import { useT } from '../hooks/useT';

export const ASSISTANT_DIALOG_ID = 'kody-assistant-dialog';

/**
 * Below 1280px the launcher is the 48px circle only, so it never sits over a page's primary action;
 * AppLayout gives <main> enough bottom padding that the last action can scroll clear of it.
 * The company setup wizard keeps the circle at every width: its sticky footer holds the primary action there.
 */
const COMPACT_ROUTES = [/^\/setup\/new-company(\/|$)/, /^\/setup\/company\/[^/]+\/complete(\/|$)/];

export function AssistantLauncher({ open, onOpen }: { open: boolean; onOpen: () => void }) {
  const t = useT();
  const pathname = usePathname() ?? '';
  const compact = COMPACT_ROUTES.some(route => route.test(pathname));

  return (
    <button
      type="button"
      onClick={onOpen}
      aria-label={t('Open Kody, HR assistant')}
      aria-haspopup="dialog"
      aria-expanded={open}
      aria-controls={open ? ASSISTANT_DIALOG_ID : undefined}
      title={t('Kody the HR Assistant')}
      className={`wg-press fixed bottom-[calc(5rem+env(safe-area-inset-bottom))] end-4 z-30 flex h-12 items-center gap-3 rounded-full border border-blue-700 bg-blue-700 text-white shadow-soft-md hover:bg-blue-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-blue-600 lg:bottom-6 lg:end-6 dark:border-blue-500 dark:bg-blue-600 dark:hover:bg-blue-500 dark:focus-visible:outline-blue-300 ${compact ? '' : 'xl:pe-5'}`}
      data-compact={compact ? 'true' : undefined}
    >
      <span aria-hidden="true" className="grid h-[46px] w-[46px] shrink-0 place-items-center text-xl font-semibold">K</span>
      <span aria-hidden="true" className={`hidden text-start leading-tight ${compact ? '' : 'xl:block'}`}>
        <span className="block text-sm font-semibold">Kody</span>
        <span className="block text-xs text-blue-100">{t('HR Assistant')}</span>
      </span>
    </button>
  );
}
