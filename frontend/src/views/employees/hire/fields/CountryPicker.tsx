'use client';

import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';
import { buildCountryOptions, searchCountries, type CountryOption } from '@/src/lib/hire/nationality';
import { FieldShell, inputClass } from './FieldShell';

/**
 * ARIA 1.2 combobox (editable, list autocomplete). Saudi Arabia is pinned first, then the GCC, then A-Z; EN and AR
 * names are searched in either script. The value is an ISO 3166 alpha-2 code. Free text that is not picked is kept as
 * "~text" so validation can say "Choose a nationality from the list" on blur. Selecting calls onSelect once (A2).
 */
export function CountryPicker({ field, label, value, onSelect, onFreeText, onBlur, error, hint, options: given }: {
  field: string; label: string; value: string; onSelect: (code: string) => void; onFreeText: (text: string) => void;
  onBlur: () => void; error?: string; hint?: string; options?: CountryOption[];
}) {
  const { locale } = useLocale();
  const listId = useId();
  const options = useMemo(() => given ?? buildCountryOptions(), [given]);
  const selected = options.find(o => o.code === value);
  const nameOf = (o: CountryOption) => (locale === 'ar' ? o.nameAr : o.nameEn);
  const [query, setQuery] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(-1);
  const [above, setAbove] = useState(false);
  const text = query ?? (selected ? nameOf(selected) : value.startsWith('~') ? value.slice(1) : '');
  const matches = useMemo(() => searchCountries(options, query ?? '', locale), [options, query, locale]);
  const wrap = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  const choose = (o: CountryOption) => { setQuery(null); setOpen(false); setActive(-1); onSelect(o.code); };

  useLayoutEffect(() => {
    if (!open || !wrap.current) return;
    const place = () => {
      const box = wrap.current!.getBoundingClientRect();
      const action = document.querySelector<HTMLElement>('[data-testid="action-bar"]')?.getBoundingClientRect();
      const lowerEdge = Math.min(window.innerHeight, action?.top ?? window.innerHeight);
      const below = lowerEdge - box.bottom - 4;
      const aboveSpace = box.top - 4;
      setAbove(below < Math.min(240, Math.max(120, aboveSpace)) && aboveSpace > below);
    };
    place();
    window.addEventListener('resize', place);
    window.addEventListener('scroll', place, true);
    return () => { window.removeEventListener('resize', place); window.removeEventListener('scroll', place, true); };
  }, [open]);

  useEffect(() => {
    const list = listRef.current;
    const option = active >= 0 ? document.getElementById(`${listId}-${matches[active]?.code}`) : null;
    if (!list || !option) return;
    const top = option.offsetTop;
    const bottom = top + option.offsetHeight;
    if (top < list.scrollTop) list.scrollTop = top;
    else if (bottom > list.scrollTop + list.clientHeight) list.scrollTop = bottom - list.clientHeight;
  }, [active, listId, matches]);

  return (
    <FieldShell field={field} label={label} error={error} hint={hint}>
      {p => (
        <div ref={wrap} className="relative" onBlur={e => { if (!wrap.current?.contains(e.relatedTarget as Node)) { setOpen(false); setQuery(null); onBlur(); } }}>
          <input {...p} role="combobox" aria-expanded={open} aria-controls={listId} aria-autocomplete="list" aria-activedescendant={open && matches[active] ? `${listId}-${matches[active].code}` : undefined}
            autoComplete="off" value={text} className={inputClass}
            onChange={e => { setQuery(e.target.value); setOpen(true); setActive(-1); onFreeText(e.target.value.trim() ? `~${e.target.value.trim()}` : ''); }}
            onFocus={() => { setOpen(true); setActive(-1); }}
            onKeyDown={e => {
              if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setActive(i => Math.min(i + 1, matches.length - 1)); }
              else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(i => Math.max(i - 1, 0)); }
              else if (e.key === 'Enter' && open && matches[active]) { e.preventDefault(); choose(matches[active]); }
              else if (e.key === 'Escape' && open) { e.preventDefault(); setOpen(false); }
            }} />
          {open && matches.length > 0 && (
            <ul ref={listRef} id={listId} role="listbox" aria-label={label} className={`absolute z-20 max-h-60 w-full overflow-auto rounded-lg border border-slate-200 bg-white p-1 text-sm shadow-lg dark:border-white/10 dark:bg-slate-900 ${above ? 'bottom-full mb-1' : 'top-full mt-1'}`}>
              {matches.map((o, i) => (
                <li key={o.code} id={`${listId}-${o.code}`} role="option" aria-selected={o.code === value} tabIndex={-1}
                  onMouseDown={e => e.preventDefault()} onClick={() => choose(o)}
                  className={`cursor-pointer rounded-md px-2.5 py-2 ${i === active ? 'bg-slate-100 dark:bg-white/10' : ''}`}>
                  <span>{nameOf(o)}</span> <span className="text-xs text-slate-500" dir="ltr">{o.code}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </FieldShell>
  );
}
