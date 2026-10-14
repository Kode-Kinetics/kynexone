'use client';

import { useRef, useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { SETUP_AREAS, type SetupAreaId } from '../../lib/setupAreas';
import { useT } from '../../hooks/useT';

export function SetupAreaNavigator({ activeArea, onSelect }: {
  activeArea: SetupAreaId | 'overview';
  onSelect: (area: SetupAreaId | 'overview') => void;
}) {
  const t = useT();
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const activeLabel = SETUP_AREAS.find(area => area.id === activeArea)?.label ?? 'Workspace overview';
  const select = (id: SetupAreaId | 'overview') => {
    onSelect(id);
    if (open) { setOpen(false); trigger.current?.focus(); }
  };
  return (
    <aside className="min-w-0 lg:w-64 lg:shrink-0">
      <button ref={trigger} type="button" aria-expanded={open} aria-controls="setup-area-navigation"
        onClick={() => setOpen(value => !value)}
        className="flex w-full items-center justify-between gap-3 rounded-lg border border-slate-300 px-3 py-3 text-start text-sm font-medium text-slate-900 focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire dark:border-white/20 dark:text-white lg:hidden">
        <span>{t('Choose setup area')}: {t(activeLabel)}</span><ChevronDown aria-hidden="true" className="h-4 w-4 shrink-0" />
      </button>
      <nav id="setup-area-navigation" aria-label={t('Setup areas')}
        onKeyDown={event => { if (event.key === 'Escape' && open) { setOpen(false); trigger.current?.focus(); } }}
        className={`${open ? 'block' : 'hidden'} mt-2 space-y-1 rounded-xl border border-slate-200 bg-white p-2 dark:border-white/10 dark:bg-slate-900 lg:mt-0 lg:block`}>
        {[{ id: 'overview' as const, label: 'Workspace overview' }, ...SETUP_AREAS].map(area => (
          <button type="button" key={area.id} onClick={() => select(area.id)} aria-current={activeArea === area.id ? 'page' : undefined}
            className={`w-full rounded-lg px-3 py-2.5 text-start text-sm leading-5 focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire ${activeArea === area.id ? 'bg-sapphire/10 font-semibold text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent' : 'text-slate-700 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/5'}`}>
            {t(area.label)}
          </button>
        ))}
      </nav>
    </aside>
  );
}
