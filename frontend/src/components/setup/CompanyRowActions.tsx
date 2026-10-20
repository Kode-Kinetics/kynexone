'use client';

import { useId, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, Pencil } from 'lucide-react';
import { useT } from '../../hooks/useT';

type Action = { label: string; onSelect: () => void; disabled?: boolean; destructive?: boolean };

/** The portal keeps the menu outside the table's scrolling/clipping container. */
export function CompanyRowActions({ companyName, onEdit, actions }: {
  companyName: string; onEdit: () => void; actions: Action[];
}) {
  const t = useT();
  const id = useId();
  const trigger = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const lastItem = useRef(false);
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState({ left: 0, top: 0 });
  const close = () => { setOpen(false); trigger.current?.focus({ preventScroll: true }); };

  useLayoutEffect(() => {
    if (!open || !menu.current || !trigger.current) return;
    const anchor = trigger.current.getBoundingClientRect();
    const { height, width } = menu.current.getBoundingClientRect();
    setPosition({
      left: Math.max(8, Math.min(anchor.right - width, window.innerWidth - width - 8)),
      top: Math.max(8, anchor.bottom + height + 8 <= window.innerHeight ? anchor.bottom + 4 : anchor.top - height - 4),
    });
    const items = menu.current.querySelectorAll<HTMLButtonElement>('button:not(:disabled)');
    items[lastItem.current ? items.length - 1 : 0]?.focus({ preventScroll: true });
    const dismiss = () => { setOpen(false); trigger.current?.focus({ preventScroll: true }); };
    const outside = (event: PointerEvent) => {
      if (!menu.current?.contains(event.target as Node) && !trigger.current?.contains(event.target as Node)) setOpen(false);
    };
    const scroll = (event: Event) => { if (!menu.current?.contains(event.target as Node)) dismiss(); };
    document.addEventListener('pointerdown', outside);
    window.addEventListener('resize', dismiss);
    window.addEventListener('scroll', scroll, true);
    return () => {
      document.removeEventListener('pointerdown', outside);
      window.removeEventListener('resize', dismiss);
      window.removeEventListener('scroll', scroll, true);
    };
  }, [open]);

  const onMenuKey = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape' || event.key === 'Tab') {
      if (event.key === 'Escape') event.preventDefault();
      event.stopPropagation(); close(); return;
    }
    const items = Array.from(menu.current?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)') ?? []);
    const current = items.indexOf(document.activeElement as HTMLButtonElement);
    let next: number;
    if (event.key === 'ArrowDown') next = (current + 1) % items.length;
    else if (event.key === 'ArrowUp') next = (current - 1 + items.length) % items.length;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = items.length - 1;
    else return;
    event.preventDefault(); items[next]?.focus();
  };

  return <div className="flex flex-col items-stretch gap-1 sm:flex-row sm:items-center">
    <button type="button" onClick={onEdit} className="btn-secondary min-h-9 whitespace-nowrap px-2 text-xs">
      <Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}
    </button>
    <button ref={trigger} type="button" aria-haspopup="menu" aria-expanded={open} aria-controls={open ? id : undefined}
      aria-label={`${t('More actions')}: ${companyName}`} className="btn-secondary min-h-9 whitespace-nowrap px-2 text-xs"
      onClick={() => { lastItem.current = false; setOpen(value => !value); }}
      onKeyDown={event => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault(); lastItem.current = event.key === 'ArrowUp'; setOpen(true);
        }
      }}>
      {t('More actions')} <ChevronDown className="h-3 w-3" aria-hidden="true" />
    </button>
    {open && createPortal(<div ref={menu} id={id} role="menu" aria-label={`${t('More actions')}: ${companyName}`}
      onKeyDown={onMenuKey} style={position}
      className="fixed z-[100] w-56 max-w-[calc(100vw-16px)] max-h-[calc(100dvh-16px)] overflow-y-auto rounded-xl border border-slate-200 bg-white p-1 shadow-xl dark:border-white/15 dark:bg-slate-900">
      {actions.map(action => <button key={action.label} type="button" role="menuitem" tabIndex={-1} disabled={action.disabled}
        onClick={() => { close(); action.onSelect(); }}
        className={`block min-h-10 w-full rounded-lg px-3 py-2 text-start text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 disabled:opacity-50 ${action.destructive ? 'text-rose-700 hover:bg-rose-50 focus:bg-rose-50 dark:text-rose-300 dark:hover:bg-rose-950 dark:focus:bg-rose-950' : 'text-slate-700 hover:bg-slate-100 focus:bg-slate-100 dark:text-slate-100 dark:hover:bg-slate-800 dark:focus:bg-slate-800'}`}>
        {t(action.label)}
      </button>)}
    </div>, document.body)}
  </div>;
}
