'use client';

import { useCallback, useState, type ReactNode } from 'react';
import { useT } from '../../hooks/useT';
import type { ConfirmTier } from '../../lib/confirmTier';
import { ConfirmDialog, type ConfirmState } from './ConfirmDialog';

// `const ok = await confirm({...})` over the ONE shared ConfirmDialog (components/ui/ConfirmDialog).
// glUi re-exports this so the GL panels, Setup and the wizard all share it.
//
// The three tiers of the Screen Standard (lib/confirmTier.ts):
//   tier 1  no dialog: `confirm` resolves true at once and the caller offers Undo for UNDO_WINDOW_MS;
//   tier 2  (default) a dialog with focus on Cancel and no Enter-to-confirm;
//   tier 3  tier 2 plus an impact preview, the name or number typed back, and a required reason.
// `ask` is `confirm` that also returns the reason (a native prompt() becomes `ask` with `reason`).

export interface ConfirmOptions {
  title: string;
  message: ReactNode;
  confirmLabel: string;
  tone?: 'danger' | 'primary';
  tier?: ConfirmTier;
  /** Tier 3: what will change. */
  impact?: ReactNode;
  /** Tier 3: the exact name or number to type back. */
  typeToConfirm?: string;
  /** Ask for a reason (tier 3 always does). */
  reason?: { label?: string; required?: boolean; placeholder?: string; minLength?: number };
}

export interface AskResult { confirmed: boolean; reason: string }

export function useConfirm() {
  const t = useT();
  const [state, setState] = useState<{ options: ConfirmOptions; resolve: (result: AskResult) => void } | null>(null);
  const ask = useCallback(
    (options: ConfirmOptions) => (options.tier === 1
      ? Promise.resolve<AskResult>({ confirmed: true, reason: '' })
      : new Promise<AskResult>((resolve) => setState({ options, resolve }))),
    [],
  );
  const confirm = useCallback((options: ConfirmOptions) => ask(options).then(result => result.confirmed), [ask]);
  const close = (confirmed: boolean, reason = '') => {
    state?.resolve({ confirmed, reason });
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
        tier: state.options.tier === 3 ? 3 : 2,
        impact: state.options.impact,
        typeToConfirm: state.options.typeToConfirm,
        reason: state.options.reason,
        onConfirm: ({ reason }) => close(true, reason),
      }
    : null;
  const dialog = <ConfirmDialog state={dialogState} onCancel={() => close(false)} />;
  return { confirm, ask, dialog };
}
