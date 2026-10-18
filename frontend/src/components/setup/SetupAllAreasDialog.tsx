'use client';

import { useEffect, useRef, type RefObject } from 'react';
import { X } from 'lucide-react';
import { SETUP_AREAS, type SetupAreaId } from '../../lib/setupAreas';
import { useT } from '../../hooks/useT';

const focusableSelector = 'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function SetupAllAreasDialog({ open, activeArea, onSelect, onClose, triggerRef }: {
  open: boolean;
  activeArea: SetupAreaId | 'overview';
  onSelect: (area: SetupAreaId | 'overview') => void;
  onClose: () => void;
  triggerRef: RefObject<HTMLButtonElement | null>;
}) {
  const t = useT();
  const dialog = useRef<HTMLDivElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    if (!open) return;
    const priorOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    heading.current?.focus();
    return () => { document.body.style.overflow = priorOverflow; };
  }, [open]);

  if (!open) return null;

  const close = () => {
    onClose();
    window.requestAnimationFrame(() => triggerRef.current?.focus());
  };
  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      close();
      return;
    }
    if (event.key !== 'Tab') return;
    const tabbable = Array.from(dialog.current?.querySelectorAll<HTMLElement>(focusableSelector) ?? []);
    if (!tabbable.length) { event.preventDefault(); return; }
    const first = tabbable[0];
    const last = tabbable[tabbable.length - 1];
    if (event.shiftKey && (document.activeElement === first || document.activeElement === heading.current)) {
      event.preventDefault(); last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault(); first.focus();
    }
  };

  return (
    <>
      <div aria-hidden="true" className="fixed inset-0 z-40 bg-slate-950/55 backdrop-blur-sm" />
      <div ref={dialog} role="dialog" aria-modal="true" aria-labelledby="setup-all-areas-title" onKeyDown={onKeyDown}
        className="fixed inset-0 z-50 flex h-[100dvh] w-screen flex-col overflow-hidden bg-white p-5 shadow-2xl dark:bg-slate-950 sm:inset-auto sm:start-1/2 sm:top-1/2 sm:h-[min(80vh,720px)] sm:w-[min(90vw,64rem)] sm:-translate-x-1/2 sm:-translate-y-1/2 sm:rounded-2xl sm:border sm:border-slate-200 sm:p-7 dark:sm:border-white/10 rtl:sm:translate-x-1/2">
        <div className="flex items-start justify-between gap-4 border-b border-slate-200 pb-4 dark:border-white/10">
          <div>
            <h2 ref={heading} tabIndex={-1} id="setup-all-areas-title" className="text-xl font-semibold text-slate-950 outline-none dark:text-white">{t('Browse all setup areas')}</h2>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{t('Choose an area. No settings are changed by navigation.')}</p>
          </div>
          <button type="button" onClick={close} aria-label={t('Close')} className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg border border-slate-200 text-slate-600 hover:bg-slate-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5">
            <X aria-hidden="true" className="h-5 w-5" />
          </button>
        </div>
        <nav aria-label={t('Setup areas')} className="min-h-0 flex-1 overflow-y-auto py-5">
          <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {[{ id: 'overview' as const, label: 'Workspace overview', description: 'Review every setup area without implying completion.' }, ...SETUP_AREAS].map(area => (
              <li key={area.id}>
                <button type="button" aria-label={t(area.label)} onClick={() => onSelect(area.id)} aria-current={activeArea === area.id ? 'page' : undefined}
                  className={`h-full w-full rounded-xl border p-4 text-start focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire ${activeArea === area.id ? 'border-sapphire bg-sapphire/10 dark:border-cyanAccent dark:bg-cyanAccent/10' : 'border-slate-200 hover:border-sapphire/50 hover:bg-sapphire/[0.03] dark:border-white/10 dark:hover:border-cyanAccent/50 dark:hover:bg-cyanAccent/[0.04]'}`}>
                  <span className="block font-semibold text-slate-950 dark:text-white">{t(area.label)}</span>
                  <span className="mt-1 block text-xs leading-5 text-slate-600 dark:text-slate-300">{t(area.description)}</span>
                </button>
              </li>
            ))}
          </ul>
        </nav>
      </div>
    </>
  );
}
