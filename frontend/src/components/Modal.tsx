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
  size?: 'sm' | 'md' | 'lg' | 'xl' | 'wizard';
  /** 'center' is a dialog; 'end' is a drawer docked to the inline-end edge (the left edge in RTL). */
  placement?: 'center' | 'end';
  headerContent?: React.ReactNode;
  closeDisabled?: boolean;
  closeLabel?: string;
}

const sizeClass = {
  sm: 'max-w-sm',
  md: 'max-w-lg',
  lg: 'max-w-2xl',
  xl: 'max-w-6xl',
  wizard: 'max-w-4xl',
};

export function Modal({ isOpen, title, onClose, children, footer, headerContent, size = 'md', placement = 'center', closeDisabled = false, closeLabel = 'Close' }: ModalProps) {
  const titleId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const closeButtonRef = useRef<HTMLButtonElement>(null);
  const onCloseRef = useRef(onClose);
  const closeDisabledRef = useRef(closeDisabled);

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    closeDisabledRef.current = closeDisabled;
  }, [closeDisabled]);

  useEffect(() => {
    if (!isOpen) return;
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const mountedDialog = dialogRef.current;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    const focusFrame = requestAnimationFrame(() => closeButtonRef.current?.focus());

    const onKey = (e: KeyboardEvent) => {
      const dialogs = document.querySelectorAll('[role="dialog"][aria-modal="true"]');
      if (dialogs.item(dialogs.length - 1) !== dialogRef.current) return;
      if (e.key === 'Escape') {
        e.preventDefault();
        if (!closeDisabledRef.current) onCloseRef.current();
        return;
      }
      if (e.key !== 'Tab' || !dialogRef.current) return;
      const focusable = Array.from(
        dialogRef.current.querySelectorAll<HTMLElement>(
          'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
        )
      ).filter((item) => !item.hasAttribute('aria-hidden') && !item.closest('[inert]') && item.getClientRects().length > 0);
      if (focusable.length === 0) {
        e.preventDefault();
        dialogRef.current.focus();
        return;
      }
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
      cancelAnimationFrame(focusFrame);
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = previousOverflow;
      // Strict Mode replays effects while the closing dialog is still in the DOM.
      const remainingDialogs = Array.from(document.querySelectorAll<HTMLElement>('[role="dialog"][aria-modal="true"]')).filter(dialog => dialog !== mountedDialog);
      const parentDialog = remainingDialogs[remainingDialogs.length - 1];
      if (parentDialog && !parentDialog.contains(previouslyFocused)) {
        parentDialog.querySelector<HTMLElement>('button:not([disabled]), a[href], input:not([disabled]), [tabindex="0"]')?.focus();
      } else {
        previouslyFocused?.focus();
      }
    };
  }, [isOpen]);

  if (!isOpen) return null;

  const drawer = placement === 'end';

  return createPortal(
    <div className={`fixed inset-0 z-50 flex ${drawer ? 'justify-end' : 'items-center justify-center p-2 sm:p-4'}`}>
      {/* Backdrop: a flat scrim. No blur, so the page behind stays legible and cheap to paint. */}
      <div
        className="absolute inset-0 bg-[var(--kx-scrim)]"
        onClick={() => { if (!closeDisabled) onClose(); }}
        aria-hidden="true"
      />

      {/* Dialog / drawer */}
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        className={`relative flex w-full flex-col overflow-hidden border border-line bg-surface-overlay shadow-e3 ${
          drawer
            ? `h-full ${sizeClass[size]} animate-slide-in-right rounded-s-2xl border-e-0 rtl:animate-slide-in-left`
            : `${sizeClass[size]} max-h-[calc(100vh-1rem)] rounded-2xl`
        }`}
      >
        {/* Header */}
        <div className="flex shrink-0 items-center justify-between border-b border-line px-4 py-3 sm:px-5">
          <h2 id={titleId} className="text-h4 font-semibold text-fg">{title}</h2>
          <button
            ref={closeButtonRef}
            type="button"
            onClick={onClose}
            disabled={closeDisabled}
            aria-label={closeLabel}
            className="grid h-control-sm w-control-sm place-items-center rounded-lg text-fg-muted transition-colors hover:bg-surface-sunken hover:text-fg"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        {headerContent && <div className="shrink-0">{headerContent}</div>}

        {/* Body: scrolls within the constrained dialog */}
        <div className="flex-1 overflow-y-auto px-3 py-3 sm:px-5 sm:py-4">{children}</div>

        {/* Footer */}
        {footer && (
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-2 border-t border-line bg-surface-sunken px-4 py-3 sm:px-5">
            {footer}
          </div>
        )}
      </div>
    </div>,
    document.body
  );
}
