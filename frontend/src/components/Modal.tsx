'use client';

import { X } from 'lucide-react';
import { useEffect, useId, useRef } from 'react';
import { createPortal } from 'react-dom';

interface ModalProps {
  isOpen: boolean;
  title: string;
  onClose: () => void;
  children: React.ReactNode;
  footer?: React.ReactNode;
  size?: 'sm' | 'md' | 'lg' | 'xl';
}

const sizeClass = {
  sm: 'max-w-sm',
  md: 'max-w-lg',
  lg: 'max-w-2xl',
  xl: 'max-w-6xl',
};

export function Modal({ isOpen, title, onClose, children, footer, size = 'md' }: ModalProps) {
  const titleId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const closeButtonRef = useRef<HTMLButtonElement>(null);
  const onCloseRef = useRef(onClose);

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    if (!isOpen) return;
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    requestAnimationFrame(() => closeButtonRef.current?.focus());

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        onCloseRef.current();
        return;
      }
      if (e.key !== 'Tab' || !dialogRef.current) return;
      const focusable = Array.from(
        dialogRef.current.querySelectorAll<HTMLElement>(
          'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
        )
      ).filter((item) => !item.hasAttribute('aria-hidden'));
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = previousOverflow;
      previouslyFocused?.focus();
    };
  }, [isOpen]);

  if (!isOpen) return null;

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center p-2 sm:p-4">
      {/* Backdrop */}
      <div
        className="absolute inset-0 bg-midnight/55 backdrop-blur-md"
        onClick={onClose}
        aria-hidden="true"
      />

      {/* Dialog */}
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className={`relative flex w-full ${sizeClass[size]} max-h-[calc(100vh-1rem)] flex-col overflow-hidden rounded-[22px] border border-white/70 bg-[#f8fbff] shadow-[0_26px_70px_rgba(15,23,42,0.28),inset_0_1px_0_rgba(255,255,255,0.95)] ring-1 ring-slate-900/5 dark:border-white/10 dark:bg-[#0D1221] dark:shadow-[0_28px_80px_rgba(0,0,0,0.55),inset_0_1px_0_rgba(255,255,255,0.08)] dark:ring-white/5`}
      >
        {/* Header */}
        <div className="flex shrink-0 items-center justify-between border-b border-slate-200/80 bg-white/75 px-4 py-3 shadow-[0_1px_0_rgba(255,255,255,0.85)_inset] backdrop-blur-sm dark:border-white/[0.07] dark:bg-white/[0.04] sm:px-5">
          <h2 id={titleId} className="text-base font-bold text-slate-950 dark:text-white">{title}</h2>
          <button
            ref={closeButtonRef}
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="grid h-8 w-8 place-items-center rounded-xl border border-slate-200/80 bg-white text-slate-400 shadow-[0_4px_12px_rgba(15,23,42,0.08),inset_0_1px_0_rgba(255,255,255,0.9)] transition hover:-translate-y-px hover:text-slate-700 dark:border-white/10 dark:bg-white/[0.06] dark:hover:text-white"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        {/* Body — scrolls within the constrained dialog */}
        <div className="flex-1 overflow-y-auto px-3 py-3 sm:px-5 sm:py-4">{children}</div>

        {/* Footer */}
        {footer && (
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-2 border-t border-slate-200/80 bg-white/80 px-4 py-3 shadow-[0_-1px_0_rgba(255,255,255,0.85)_inset] backdrop-blur-sm dark:border-white/[0.07] dark:bg-white/[0.04] sm:px-5">
            {footer}
          </div>
        )}
      </div>
    </div>,
    document.body
  );
}
