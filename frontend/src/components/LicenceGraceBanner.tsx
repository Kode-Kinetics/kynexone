'use client';

import { useEffect, useState } from 'react';
import { LICENCE_GRACE_EVENT } from '../api/client';

/**
 * Shown while the installation's licence is past its expiry but inside its grace days. Everything still
 * works; the API marks each response with `X-Licence-Status: Grace` and this strip says so until the
 * administrator imports a renewed licence. Dismissing it hides it for this browser session only.
 */
export function LicenceGraceBanner() {
  const [inGrace, setInGrace] = useState(false);
  const [dismissed, setDismissed] = useState(false);

  useEffect(() => {
    try { setDismissed(sessionStorage.getItem('kx_licence_grace_dismissed') === '1'); } catch { /* storage may be blocked */ }
    const onGrace = () => setInGrace(true);
    window.addEventListener(LICENCE_GRACE_EVENT, onGrace);
    return () => window.removeEventListener(LICENCE_GRACE_EVENT, onGrace);
  }, []);

  if (!inGrace || dismissed) return null;

  return (
    <div role="status" className="flex items-center justify-between gap-3 border-b border-amber-300 bg-amber-50 px-4 py-2 text-sm text-amber-900">
      <span>
        Your KynexOne licence has expired and is in its grace period. Everything keeps working for now — ask your
        administrator to import the renewed licence under Tenant Admin → Licence.
      </span>
      <button
        type="button"
        className="shrink-0 rounded px-2 py-1 text-xs font-medium hover:bg-amber-100"
        onClick={() => {
          setDismissed(true);
          try { sessionStorage.setItem('kx_licence_grace_dismissed', '1'); } catch { /* ignore */ }
        }}
      >
        Dismiss
      </button>
    </div>
  );
}
