'use client';

import { createContext, useContext, useLayoutEffect } from 'react';

/** Canonical editors report their own edit/save lifecycle; filters never enter it. */
export const SetupEditorState = createContext<((dirty: boolean) => void) | undefined>(undefined);
export const SetupPendingFilesState = createContext<((pending: boolean) => void) | undefined>(undefined);

export function useSetupEditorState(dirty: boolean) {
  const report = useContext(SetupEditorState);
  useLayoutEffect(() => { report?.(dirty); return () => report?.(false); }, [dirty, report]);
}
