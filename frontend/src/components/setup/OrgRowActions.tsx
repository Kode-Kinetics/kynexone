'use client';

import { useCallback, useState, type ReactNode } from 'react';
import { Pencil, RotateCcw, Trash2 } from 'lucide-react';
import { notifyApiError } from '../../api/client';
import { orgRowsApi, type OrgRowKind, type OrgRowRemoval } from '../../api/orgRows';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { useT } from '../../hooks/useT';
import { msg } from '../../i18n/translations';
import { useConfirm } from '../ui/useConfirm';
import { usageSentence } from '../../lib/orgUsage';

/**
 * Setup's one add/remove rule for organisation rows (owner decision 2026-10-25), shared by every Setup tab and the wizard:
 * every row has Edit and Remove, and Restore once it is turned off. A blocked action is shown disabled with the reason,
 * never hidden. Remove is decided by the server (never used = deleted; in use = turned off), so the dialog names both
 * possible effects and the result says which one happened.
 */

const NAME: Record<OrgRowKind, string> = {
  branch: msg('branch'), department: msg('department'), designation: msg('designation'), grade: msg('grade'),
  costCenter: msg('cost centre'), location: msg('location'), masterDataValue: msg('drop-down value'),
};

/** Whether the signed-in person may write a row of this company, by the same rule the API applies (group grant, else the
 *  row's company within their grants). A null company is tenant-wide: group scope only. */
export function useOrgWriteAccess(permission: string | null = 'organization.write'): (companyId: string | null | undefined) => { allowed: boolean; reason?: string } {
  const t = useT();
  const { hasPermission } = useAuth();
  const { isGroupScope, companies } = useCompany();
  // null: the server's role gate decides the action; the screen only checks company scope.
  const mayWrite = permission === null || hasPermission(permission);
  return useCallback((companyId) => {
    if (!mayWrite) return { allowed: false, reason: t('You do not have permission to change this.') };
    if (isGroupScope) return { allowed: true };
    if (companyId && companies.some(c => c.id === companyId)) return { allowed: true };
    return { allowed: false, reason: t('This belongs to another company or to the whole group. Ask a group administrator.') };
  }, [mayWrite, isGroupScope, companies, t]);
}

/** The two calls a Setup list needs. Organisation rows use `orgRowsApi`; any other Setup row passes its own pair, which
 *  must return the same body (`outcome`, `message`, `usage`). `acknowledge` is sent when the person confirmed a warning. */
export interface RemovalApi {
  remove: (id: string, opts?: { acknowledge?: boolean }) => Promise<OrgRowRemoval>;
  restore: (id: string) => Promise<unknown>;
}

export function useOrgRowRemoval({ kind, api, noun, warning, onDone }: {
  kind?: OrgRowKind;
  /** Another Setup row type than the organisation rows: its own remove / restore calls. */
  api?: RemovalApi;
  /** What the row is called in the dialog ("leave type"); required with `api`. Pass it through msg() so it translates. */
  noun?: string;
  /** Extra warning the person must accept in the dialog for a given row (statutory leave, system role...). */
  warning?: (row: { id: string; name: string }) => string | null | undefined;
  onDone: () => void;
}) {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [busyId, setBusyId] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ text: string; tone: 'ok' } | null>(null);

  const remove = useCallback(async (row: { id: string; name: string }) => {
    const what = t(noun ?? NAME[kind!]);
    const warn = warning?.(row) ?? null;
    const ok = await confirm({
      title: t('Remove {what}', { what }),
      message: (
        <div className="space-y-2">
          <p><bdi>{t('Remove {name}?', { name: row.name })}</bdi></p>
          <p className="text-slate-500 dark:text-slate-400">
            {t('If nothing uses it, it is deleted. If employees or other records use it, it is turned off instead: it stays in history, disappears from new choices, and you can restore it anytime.')}
          </p>
          {warn && <p role="alert" data-testid="remove-warning" className="rounded-lg border border-amber-300 bg-amber-50 px-2 py-1.5 text-amber-900 dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100">{warn}</p>}
        </div>
      ),
      confirmLabel: t('Remove'),
      tone: 'danger',
    });
    if (!ok) return;
    setBusyId(row.id);
    try {
      const send = (acknowledge: boolean): Promise<OrgRowRemoval> => api ? api.remove(row.id, acknowledge ? { acknowledge: true } : undefined) : orgRowsApi.remove(kind!, row.id);
      let result: OrgRowRemoval;
      try { result = await send(!!warn); }
      catch (e) {
        // The server decides what is statutory (it knows more than the row the list shows): it refuses with a stable *_STATUTORY
        // code and its own sentence, and the person confirms that sentence before the call is repeated with the acknowledgement.
        const res = (e as { response?: { status?: number; data?: { code?: string; message?: string } } }).response;
        if (warn || !api || res?.status !== 409 || !res.data?.code?.endsWith('_STATUTORY')) throw e;
        const again = await confirm({ title: t('Remove {what}', { what }), message: <p role="alert" data-testid="remove-warning">{res.data.message}</p>, confirmLabel: t('Remove'), tone: 'danger' });
        if (!again) return;
        result = await send(true);
      }
      setNotice({ tone: 'ok', text: result.outcome === 'deleted' ? t('Removed {name}.', { name: row.name }) : t('Turned off {name}. {reason}', { name: row.name, reason: result.usage.summary ? `${usageSentence(result.usage.items, t, result.usage.summary)}. ${t('Restore anytime.')}` : t('Restore anytime.') }) });
      onDone();
    } catch (e) { notifyApiError(e); }
    finally { setBusyId(null); }
  }, [confirm, kind, api, noun, warning, onDone, t]);

  const restore = useCallback(async (row: { id: string; name: string }) => {
    setBusyId(row.id);
    try {
      await (api ? api.restore(row.id) : orgRowsApi.restore(kind!, row.id));
      setNotice({ tone: 'ok', text: t('Turned {name} back on.', { name: row.name }) });
      onDone();
    } catch (e) { notifyApiError(e); }
    finally { setBusyId(null); }
  }, [kind, api, onDone, t]);

  const noticeEl: ReactNode = notice && (
    <div role="status" data-testid="org-row-notice" className="mb-3 flex items-start justify-between gap-3 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-900 dark:border-emerald-400/30 dark:bg-emerald-500/10 dark:text-emerald-100">
      <span><bdi>{notice.text}</bdi></span>
      <button type="button" className="shrink-0 font-semibold underline" onClick={() => setNotice(null)}>{t('Dismiss')}</button>
    </div>
  );

  return { remove, restore, busyId, confirmDialog, notice: noticeEl };
}

const REMOVE_BUTTON = 'inline-flex h-7 items-center gap-1 rounded-lg border border-slate-200 px-2 text-xs font-medium text-slate-600 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-600 disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:border-slate-200 disabled:hover:bg-transparent disabled:hover:text-slate-600 dark:border-white/10 dark:text-slate-300 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400';

/** Edit | Remove (| Restore) for one row. `blockedReason` disables all three and says why, visibly. */
export function OrgRowActions({ name, isActive, busy, onEdit, onRemove, onRestore, blockedReason, removeBlockedReason, extra, editable = true }: {
  name: string;
  isActive: boolean;
  busy?: boolean;
  onEdit: () => void;
  onRemove: () => void;
  onRestore: () => void;
  blockedReason?: string;
  /** Blocks only Remove (Edit and Restore stay available), e.g. "This is the default template". Shown as text beside the row. */
  removeBlockedReason?: string;
  extra?: ReactNode;
  /** False where editing happens elsewhere (the Establishment panel): the Edit button is left out. */
  editable?: boolean;
}) {
  const t = useT();
  const blocked = !!blockedReason;
  return (
    <div className="space-y-1">
      <div className="flex flex-wrap items-center gap-1">
        {editable && (
          <button type="button" onClick={onEdit} disabled={blocked} title={blockedReason} className="btn-secondary h-7 px-2 text-xs disabled:cursor-not-allowed disabled:opacity-50">
            <Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}<span className="sr-only"> {name}</span>
          </button>
        )}
        {!isActive && (
          <button type="button" onClick={onRestore} disabled={blocked || busy} title={blockedReason} className={REMOVE_BUTTON} data-testid="org-row-restore">
            <RotateCcw className="h-3 w-3" aria-hidden="true" /> {t('Restore')}<span className="sr-only"> {name}</span>
          </button>
        )}
        <button type="button" onClick={onRemove} disabled={blocked || busy || !!removeBlockedReason} title={blockedReason ?? removeBlockedReason} className={REMOVE_BUTTON} data-testid="org-row-remove">
          <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Remove')}<span className="sr-only"> {name}</span>
        </button>
        {extra}
      </div>
      {(blockedReason || removeBlockedReason) && <p className="max-w-[16rem] text-caption leading-4 text-slate-500 dark:text-slate-400" data-testid="org-row-blocked-reason">{blockedReason ?? removeBlockedReason}</p>}
    </div>
  );
}

/** "Show turned-off" toggle for a tab's toolbar. */
export function ShowTurnedOffToggle({ checked, onChange, count }: { checked: boolean; onChange: (value: boolean) => void; count?: number }) {
  const t = useT();
  if (count === 0 && !checked) return null;
  return (
    <label className="flex items-center gap-1.5 text-xs text-slate-600 dark:text-slate-400">
      <input type="checkbox" checked={checked} onChange={e => onChange(e.target.checked)} data-testid="show-turned-off" />
      {count === undefined ? t('Show turned-off') : t('Show turned-off ({count})', { count })}
    </label>
  );
}

/** On / Off as a read-only pill. Turning a row off or on is Remove / Restore (permissioned and audited), never an edit. */
export function ReadOnlyStatus({ active }: { active: boolean }) {
  const t = useT();
  return (
    <div className="space-y-1" data-testid="read-only-status">
      <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-semibold ${active ? 'bg-emeraldZ/10 text-emeraldZ dark:bg-emeraldZ/20' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400'}`}>
        {active ? t('Active') : t('Off')}
      </span>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('Use Remove / Restore to turn it off or on.')}</p>
    </div>
  );
}
