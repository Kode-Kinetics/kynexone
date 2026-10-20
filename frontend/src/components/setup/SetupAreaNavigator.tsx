'use client';

import { useRef, useState } from 'react';
import { LayoutGrid } from 'lucide-react';
import { type SetupAreaId } from '../../lib/setupAreas';
import { useT } from '../../hooks/useT';
import { SetupAllAreasDialog } from './SetupAllAreasDialog';

export function SetupAreaNavigator({ activeArea, onSelect }: {
  activeArea: SetupAreaId | 'overview';
  onSelect: (area: SetupAreaId | 'overview', afterAccepted: () => void) => void;
}) {
  const t = useT();
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  return (
    <>
      <button ref={trigger} type="button" aria-expanded={open} aria-haspopup="dialog" onClick={() => setOpen(true)}
        className="btn-secondary inline-flex min-h-11 min-w-11 items-center gap-2 text-sm">
        <LayoutGrid aria-hidden="true" className="h-4 w-4" />
        {t('Browse all areas')}
      </button>
      <SetupAllAreasDialog
        open={open}
        activeArea={activeArea}
        triggerRef={trigger}
        onClose={() => setOpen(false)}
        onSelect={area => onSelect(area, () => setOpen(false))}
      />
    </>
  );
}
