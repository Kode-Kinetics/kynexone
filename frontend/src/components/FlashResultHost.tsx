'use client';

import { usePathname } from 'next/navigation';
import { useEffect, useState } from 'react';
import { ResultBanner } from './ui/ResultBanner';
import { takeFlashResult, type FlashResult } from '../lib/flashResult';

/** Shows the result a previous screen left for this one (lib/flashResult.ts), until the user dismisses it. */
export function FlashResultHost() {
  const pathname = usePathname();
  const [flash, setFlash] = useState<FlashResult | null>(null);
  useEffect(() => {
    const taken = takeFlashResult(window.sessionStorage, pathname ?? window.location.pathname);
    if (taken) setFlash(taken);
  }, [pathname]);
  if (!flash) return null;
  return (
    <ResultBanner
      className="mb-4"
      changed={flash.changed}
      where={flash.where}
      pending={flash.pending}
      next={flash.next}
      onDismiss={() => setFlash(null)}
    />
  );
}
