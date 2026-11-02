'use client';

import { useEffect, useState } from 'react';
import { isCapabilityOff, loadCapabilities, type Capabilities } from '../api/capabilities';

/** `off(key)` is true when the server reports the feature as not available in this build. */
export function useCapabilities() {
  const [capabilities, setCapabilities] = useState<Capabilities | null>(null);
  useEffect(() => {
    let live = true;
    void loadCapabilities().then(c => { if (live) setCapabilities(c); });
    return () => { live = false; };
  }, []);
  return { capabilities, off: (key: string) => isCapabilityOff(capabilities, key) };
}
