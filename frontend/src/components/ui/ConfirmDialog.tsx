'use client';

import { Modal } from '@/src/components/Modal';

export interface ConfirmState {
  message: string;
  onConfirm: () => void;
}

const btnGhost = 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5';
const btnDanger = 'inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50';

export function ConfirmDialog({
  state,
  onCancel,
}: {
  state: ConfirmState | null;
  onCancel: () => void;
}) {
  return (
    <Modal
      isOpen={!!state}
      title="Confirm"
      onClose={onCancel}
      size="sm"
      footer={
        <>
          <button type="button" className={btnGhost} onClick={onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className={btnDanger}
            onClick={() => { state?.onConfirm(); onCancel(); }}
          >
            Confirm
          </button>
        </>
      }
    >
      <p className="text-sm text-slate-700 dark:text-slate-300">{state?.message}</p>
    </Modal>
  );
}
