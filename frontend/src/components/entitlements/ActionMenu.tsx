'use client';

import Link from 'next/link';
import { Ellipsis } from 'lucide-react';
import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

export interface ActionMenuItem {
  key: string;
  label: string;
  /** A button item runs this; a link item (`href`) navigates. */
  onSelect?: () => void;
  href?: string;
}

/**
 * A small actions menu: a button that opens a list of actions (WAI-ARIA menu button). The list is drawn in a portal on
 * the page, positioned from the button, so a scroller or a sticky column around the button never clips it. Esc closes it
 * and returns focus to the button; arrow keys, Home and End move between items; Tab leaves and closes it.
 *
 *   `iconOnly`  the button is a 28 px icon button named by `label` (44 px on a coarse pointer)
 *   otherwise   the button shows `label` with an ellipsis icon
 */
export function ActionMenu({ label, items, iconOnly = false, className = '', children }: {
  label: string; items: ActionMenuItem[]; iconOnly?: boolean; className?: string;
  /** Replaces the default ellipsis + label content of a text button. */
  children?: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState<{ top: number; left: number } | null>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const id = useId();

  const close = useCallback((returnFocus: boolean) => {
    setOpen(false); setPosition(null);
    if (returnFocus) trigger.current?.focus();
  }, []);

  // Position from the button once the list is measured: aligned to the button's inline-end edge, flipped above when
  // there is no room below, and clamped inside the viewport.
  useLayoutEffect(() => {
    if (!open || !trigger.current || !list.current) return;
    const anchor = trigger.current.getBoundingClientRect();
    const { offsetWidth: width, offsetHeight: height } = list.current;
    const rtl = document.documentElement.dir === 'rtl';
    const margin = 8;
    const preferred = rtl ? anchor.left : anchor.right - width;
    const left = Math.min(Math.max(margin, preferred), window.innerWidth - width - margin);
    const below = anchor.bottom + 4;
    const top = below + height > window.innerHeight - margin ? Math.max(margin, anchor.top - height - 4) : below;
    setPosition({ top, left });
  }, [open]);

  // Focus moves into the list once it is placed (a hidden list cannot take focus).
  useEffect(() => {
    if (open && position) list.current?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
  }, [open, position]);

  useEffect(() => {
    if (!open) return;
    const outside = (e: MouseEvent) => {
      const target = e.target as Node;
      if (list.current?.contains(target) || trigger.current?.contains(target)) return;
      close(false);
    };
    const away = () => close(false);
    document.addEventListener('mousedown', outside);
    window.addEventListener('resize', away);
    window.addEventListener('scroll', away, true);
    return () => {
      document.removeEventListener('mousedown', outside);
      window.removeEventListener('resize', away);
      window.removeEventListener('scroll', away, true);
    };
  }, [open, close]);

  const onKeyDown = (e: React.KeyboardEvent) => {
    const entries = Array.from(list.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? []);
    const at = entries.indexOf(document.activeElement as HTMLElement);
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(true); }
    else if (e.key === 'Tab') close(false);
    else if (e.key === 'ArrowDown') { e.preventDefault(); entries[(at + 1) % entries.length]?.focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); entries[(at - 1 + entries.length) % entries.length]?.focus(); }
    else if (e.key === 'Home') { e.preventDefault(); entries[0]?.focus(); }
    else if (e.key === 'End') { e.preventDefault(); entries.at(-1)?.focus(); }
  };

  const itemClass = 'flex min-h-9 w-full items-center rounded-lg px-3 py-1.5 text-start text-body text-fg hover:bg-surface-sunken focus-visible:bg-surface-sunken';
  return <>
    <button ref={trigger} type="button" aria-haspopup="menu" aria-expanded={open} aria-controls={open ? id : undefined}
      aria-label={iconOnly ? label : undefined} onClick={() => (open ? close(false) : setOpen(true))}
      className={iconOnly
        ? `grid h-7 w-7 shrink-0 place-items-center rounded-lg text-fg-muted transition-colors duration-fast hover:bg-surface-sunken hover:text-fg [@media(pointer:coarse)]:h-11 [@media(pointer:coarse)]:w-11 ${className}`.trim()
        : `btn-secondary btn-sm ${className}`.trim()}>
      {iconOnly ? <Ellipsis className="h-4 w-4" aria-hidden /> : children ?? <>{label}<Ellipsis className="h-4 w-4" aria-hidden /></>}
    </button>
    {open && createPortal(
      <div ref={list} id={id} role="menu" aria-label={label} onKeyDown={onKeyDown}
        style={position ? { top: position.top, left: position.left } : { top: 0, left: 0, visibility: 'hidden' }}
        className="fixed z-[60] min-w-[12rem] max-w-[min(20rem,calc(100vw-1rem))] rounded-lg border border-line bg-surface-overlay p-1 shadow-e2">
        {items.map(item => item.href
          ? <Link key={item.key} role="menuitem" href={item.href} className={itemClass} onClick={() => close(false)}>{item.label}</Link>
          : <button key={item.key} type="button" role="menuitem" className={itemClass} onClick={() => { close(true); item.onSelect?.(); }}>{item.label}</button>)}
      </div>,
      document.body,
    )}
  </>;
}
