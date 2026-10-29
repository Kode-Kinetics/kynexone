'use client';

import { useCallback, useState, type ReactNode } from 'react';
import { useT } from '../../hooks/useT';
import { ConfirmDialog, type ConfirmState } from './ConfirmDialog';

// `const ok = await confirm({...})` over the ONE shared ConfirmDialog (components/ui/ConfirmDialog).
// glUi re-exports this so the GL panels, Setup and the wizard all share it.

export interface ConfirmOptions {
  title: string;
  message: ReactNode;
  confirmLabel: string;
  tone?: 'danger' | 'primary';
}

export function useConfirm() {
  const t = useT();
  const [state, setState] = useState<{ options: ConfirmOptions; resolve: (ok: boolean) => void } | null>(null);
  const confirm = useCallback(
    (options: ConfirmOptions) => new Promise<boolean>((resolve) => setState({ options, resolve })),
    [],
  );
  const close = (ok: boolean) => {
    state?.resolve(ok);
    setState(null);
  };
  const dialogState: ConfirmState | null = state
    ? {
        title: state.options.title,
        message: state.options.message,
        confirmLabel: state.options.confirmLabel,
        cancelLabel: t('Cancel'),
        tone: state.options.tone === 'primary' ? 'primary' : 'danger',
        closeOnConfirm: false,
        onConfirm: () => close(true),
      }
    : null;
  const dialog = <ConfirmDialog state={dialogState} onCancel={() => close(false)} />;
  return { confirm, dialog };
}
