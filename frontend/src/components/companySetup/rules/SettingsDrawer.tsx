'use client';

import { useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { useT } from '../../../hooks/useT';
import styles from './motion.module.css';

type Phase = 'closed' | 'entering' | 'open' | 'closing';
const EXIT_MS = 150;

const reducedMotion = () => {
  try { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; } catch { return true; }
};

/**
 * A side panel that opens a setting in place, beside the wizard (from the inline end; mirrored in Arabic).
 * Modal for focus: Tab stays inside, Esc closes (only when it is the top dialog — an editor's own dialog opened
 * from inside it closes first), and focus returns to whatever opened it. Below it the wizard keeps its state.
 * The editor's own dialogs (z-50, portalled later) open above it.
 */
export function SettingsDrawer({ open, title, description, onClose, children, testId = 'settings-drawer', wide = false }: {
  open: boolean;
  /** For an editor with a wide toolbar (e.g. Statutory rules): up to 52rem instead of 40rem. */
  wide?: boolean;
  title: string;
  description?: string;
  onClose: () => void;
  children: ReactNode;
  testId?: string;
}) {
  const t = useT();
  const titleId = useId();
  const descriptionId = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const openerRef = useRef<HTMLElement | null>(null);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;
  const [phase, setPhase] = useState<Phase>('closed');

  // Mount, then flip to open on the next frame so the entrance transition runs; unmount after the exit.
  useLayoutEffect(() => {
    if (open) {
      if (phase === 'open' || phase === 'entering') return;
      openerRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      setPhase('entering');
      return;
    }
    if (phase === 'closed' || phase === 'closing') return;
    if (reducedMotion()) { setPhase('closed'); return; }
    setPhase('closing');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  useEffect(() => {
    if (phase === 'entering') {
      const frame = requestAnimationFrame(() => requestAnimationFrame(() => setPhase('open')));
      return () => cancelAnimationFrame(frame);
    }
    if (phase === 'closing') {
      const timer = window.setTimeout(() => setPhase('closed'), EXIT_MS);
      return () => window.clearTimeout(timer);
    }
    if (phase === 'closed' && openerRef.current) {
      const opener = openerRef.current;
      openerRef.current = null;
      if (opener.isConnected) opener.focus();
    }
    return undefined;
  }, [phase]);

  const mounted = phase !== 'closed';

  // Focus, scroll lock, Esc and the Tab loop, while mounted.
  useEffect(() => {
    if (!mounted) return;
    const panel = panelRef.current;
    const focusFrame = requestAnimationFrame(() => closeRef.current?.focus());
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    const onKey = (event: KeyboardEvent) => {
      const dialogs = document.querySelectorAll('[role="dialog"][aria-modal="true"]');
      if (dialogs.item(dialogs.length - 1) !== panel) return;
      if (event.key === 'Escape') { event.preventDefault(); onCloseRef.current(); return; }
      if (event.key !== 'Tab' || !panel) return;
      const focusable = Array.from(panel.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      )).filter(el => !el.closest('[inert]') && el.getClientRects().length > 0);
      if (focusable.length === 0) { event.preventDefault(); panel.focus(); return; }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && (document.activeElement === first || !panel.contains(document.activeElement))) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && (document.activeElement === last || !panel.contains(document.activeElement))) { event.preventDefault(); first.focus(); }
    };
    document.addEventListener('keydown', onKey);
    return () => {
      cancelAnimationFrame(focusFrame);
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = previousOverflow;
    };
  }, [mounted]);

  if (!mounted || typeof document === 'undefined') return null;
  const state = phase === 'open' ? 'open' : phase === 'closing' ? 'closing' : 'entering';

  return createPortal(
    <div className="fixed inset-0 z-[45]" data-testid={`${testId}-layer`}>
      <div aria-hidden="true" data-state={state} className={`${styles.scrim} absolute inset-0 bg-midnight/30 backdrop-blur-[2px]`} onClick={() => onCloseRef.current()} />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={description ? descriptionId : undefined}
        tabIndex={-1}
        data-state={state}
        data-testid={testId}
        className={`${styles.drawer} absolute inset-y-0 end-0 flex w-full ${wide ? 'max-w-[52rem] sm:w-[min(52rem,94vw)]' : 'max-w-[40rem] sm:w-[min(40rem,92vw)]'} flex-col border-s border-slate-200/80 bg-[#f8fbff] shadow-[0_24px_64px_rgba(15,23,42,0.22)] outline-none dark:border-white/10 dark:bg-[#0D1221]`}
      >
        <header className="flex shrink-0 items-start justify-between gap-3 border-b border-slate-200/80 bg-white/80 px-4 py-3 backdrop-blur-sm dark:border-white/[0.07] dark:bg-white/[0.04] sm:px-5">
          <div className="min-w-0">
            <h2 id={titleId} className="text-base font-semibold leading-6 text-slate-950 dark:text-white">{title}</h2>
            {description && <p id={descriptionId} className="mt-0.5 text-xs leading-5 text-slate-600 dark:text-slate-400">{description}</p>}
          </div>
          <button ref={closeRef} type="button" onClick={() => onCloseRef.current()} data-testid={`${testId}-close`}
            className="inline-flex h-8 shrink-0 items-center gap-1.5 rounded-lg border border-slate-200/90 bg-white px-2.5 text-sm font-medium text-slate-700 transition-colors duration-150 hover:bg-slate-50 hover:text-slate-950 active:bg-slate-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sapphire dark:border-white/10 dark:bg-white/[0.06] dark:text-slate-200 dark:hover:bg-white/10">
            <X className="h-4 w-4" aria-hidden="true" />{t('Close')}
          </button>
        </header>
        <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-4 sm:px-5">{children}</div>
      </div>
    </div>,
    document.body,
  );
}
