'use client';

import { useState } from 'react';
import { AlertTriangle, CheckCircle2, Circle, Clock, FileUp, Wallet } from 'lucide-react';
import type { ReadinessItem, ReadinessView } from '../api/employees';

// Progress-first activation checklist. Rendered identically from the live GET /readiness
// response and the 422 employee_not_activatable body (both satisfy ReadinessView), so the
// checklist is the single source of truth — never a hand-rolled red banner (§8.3).

export interface ReadinessChecklistProps {
  readiness: ReadinessView;
  /** Save one inline field gap. Returns ok=false (+message) to surface a non-blocking notice (e.g. 202 approval). */
  onFixField?: (target: string, value: string) => Promise<{ ok: boolean; message?: string }>;
  /** Open the document-upload control pre-set to this documentType. */
  onFixDocument?: (documentType: string) => void;
  /** Whether an inline field edit can persist (some payroll sub-fields have no post-create edit path). */
  isFieldEditable?: (target: string) => boolean;
  /** Show the satisfied ("Complete") items — on in the drawer, off in the compact 422 render. */
  showPresent?: boolean;
  /** Compliance disclaimer is always shown; pass false only where the surrounding surface already shows it. */
  showDisclaimer?: boolean;
}

function whyLabel(item: ReadinessItem): string | null {
  const parts: string[] = [];
  if (item.jurisdiction) parts.push(`Required by ${item.jurisdiction} policy`);
  else if (item.reason === 'statutory') parts.push('Statutory requirement');
  else if (item.reason) parts.push(item.reason);
  return parts.length > 0 ? parts.join(' · ') : null;
}

function isDateTarget(target: string): boolean {
  return /(date|expiry)$/i.test(target);
}

function ItemRow({
  item,
  accent,
  onFixField,
  onFixDocument,
  isFieldEditable,
}: {
  item: ReadinessItem;
  accent: 'rose' | 'amber' | 'slate';
  onFixField?: ReadinessChecklistProps['onFixField'];
  onFixDocument?: ReadinessChecklistProps['onFixDocument'];
  isFieldEditable?: ReadinessChecklistProps['isFieldEditable'];
}) {
  const [open, setOpen] = useState(false);
  const [value, setValue] = useState('');
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState('');

  const dotColor = accent === 'rose' ? 'text-rose-500' : accent === 'amber' ? 'text-amber-500' : 'text-slate-400';
  const why = whyLabel(item);

  const fieldTarget = item.fix?.kind === 'field' ? item.fix.target : undefined;
  const docType = item.fix?.kind === 'document' ? item.fix.documentType : undefined;
  const canEditField = fieldTarget !== undefined && (isFieldEditable ? isFieldEditable(fieldTarget) : true) && !!onFixField;

  const save = async () => {
    if (!fieldTarget || !onFixField || !value.trim()) return;
    setSaving(true);
    setNotice('');
    try {
      const res = await onFixField(fieldTarget, value.trim());
      if (res.ok) {
        setOpen(false);
        setValue('');
      } else {
        setNotice(res.message ?? 'Submitted.');
      }
    } catch {
      setNotice('Could not save — please try again.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <li className="rounded-lg border border-slate-200 p-2.5 dark:border-white/10">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="flex items-center gap-1.5 text-sm font-medium text-slate-800 dark:text-slate-100">
            <Circle className={`h-3 w-3 shrink-0 ${dotColor}`} aria-hidden="true" />
            <span className="truncate">{item.label}</span>
          </p>
          {why && <p className="mt-0.5 pl-5 text-[11px] text-slate-500 dark:text-slate-400">{why}</p>}
          {notice && <p className="mt-1 pl-5 text-[11px] font-medium text-amber-700 dark:text-amber-400">{notice}</p>}
        </div>
        <div className="shrink-0">
          {docType && onFixDocument ? (
            <button
              type="button"
              onClick={() => onFixDocument(docType)}
              className="inline-flex items-center gap-1 rounded-md border border-slate-200 px-2 py-1 text-[11px] font-semibold text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5"
            >
              <FileUp className="h-3 w-3" aria-hidden="true" />
              Upload
            </button>
          ) : canEditField && !open ? (
            <button
              type="button"
              onClick={() => setOpen(true)}
              className="rounded-md border border-slate-200 px-2 py-1 text-[11px] font-semibold text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5"
            >
              Add
            </button>
          ) : !canEditField && item.fix?.kind === 'field' ? (
            <span className="text-[11px] text-slate-400">In profile</span>
          ) : null}
        </div>
      </div>
      {open && canEditField && (
        <div className="mt-2 flex items-center gap-2 pl-5">
          <input
            autoFocus
            type={isDateTarget(fieldTarget!) ? 'date' : 'text'}
            value={value}
            onChange={(e) => setValue(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') save(); }}
            placeholder={item.label}
            aria-label={item.label}
            className="input h-8 flex-1 text-sm"
          />
          <button
            type="button"
            onClick={save}
            disabled={saving || !value.trim()}
            className="btn-primary h-8 px-2.5 text-xs disabled:opacity-50"
          >
            {saving ? 'Saving…' : 'Save'}
          </button>
          <button
            type="button"
            onClick={() => { setOpen(false); setValue(''); setNotice(''); }}
            className="btn-secondary h-8 px-2.5 text-xs"
          >
            Cancel
          </button>
        </div>
      )}
    </li>
  );
}

function Section({
  title,
  icon,
  items,
  accent,
  emptyHidden = true,
  ...handlers
}: {
  title: string;
  icon: React.ReactNode;
  items: ReadinessItem[];
  accent: 'rose' | 'amber' | 'slate';
  emptyHidden?: boolean;
  onFixField?: ReadinessChecklistProps['onFixField'];
  onFixDocument?: ReadinessChecklistProps['onFixDocument'];
  isFieldEditable?: ReadinessChecklistProps['isFieldEditable'];
}) {
  if (items.length === 0 && emptyHidden) return null;
  return (
    <div>
      <p className="mb-1.5 flex items-center gap-1.5 text-xs font-bold uppercase tracking-wide text-slate-500 dark:text-slate-400">
        {icon}
        {title} <span className="font-semibold text-slate-400">· {items.length}</span>
      </p>
      <ul className="space-y-1.5">
        {items.map((item) => (
          <ItemRow key={item.key} item={item} accent={accent} {...handlers} />
        ))}
      </ul>
    </div>
  );
}

export function ReadinessChecklist({
  readiness,
  onFixField,
  onFixDocument,
  isFieldEditable,
  showPresent = false,
  showDisclaimer = true,
}: ReadinessChecklistProps) {
  const { progress } = readiness;
  const remaining = Math.max(0, progress.requiredTotal - progress.present);
  const pct = progress.requiredTotal > 0 ? Math.round((progress.present / progress.requiredTotal) * 100) : 100;
  const handlers = { onFixField, onFixDocument, isFieldEditable };

  return (
    <div className="space-y-3">
      {/* Lead with progress — deficit-last framing keeps the gate from feeling punitive. */}
      <div>
        <p className="text-sm font-semibold text-slate-800 dark:text-slate-100">
          {progress.present} of {progress.requiredTotal} complete{remaining > 0 ? ` — ${remaining} to go` : ' — all set'}
        </p>
        <div className="mt-1.5 h-1.5 overflow-hidden rounded-full bg-slate-100 dark:bg-white/10">
          <div
            className={`h-full rounded-full ${remaining === 0 ? 'bg-emeraldZ' : 'bg-sapphire'}`}
            style={{ width: `${pct}%` }}
          />
        </div>
      </div>

      <Section title="Required to activate" icon={<AlertTriangle className="h-3.5 w-3.5 text-rose-500" aria-hidden="true" />} items={readiness.blocking} accent="rose" {...handlers} />
      <Section title="Required to pay" icon={<Wallet className="h-3.5 w-3.5 text-amber-500" aria-hidden="true" />} items={readiness.payBlocking} accent="amber" {...handlers} />
      <Section title="Expiring soon" icon={<Clock className="h-3.5 w-3.5 text-amber-500" aria-hidden="true" />} items={readiness.expiringSoon ?? []} accent="amber" {...handlers} />
      <Section title="Recommended" icon={<Circle className="h-3.5 w-3.5 text-slate-400" aria-hidden="true" />} items={readiness.recommended} accent="slate" {...handlers} />

      {showPresent && (readiness.present?.length ?? 0) > 0 && (
        <details className="rounded-lg border border-slate-200 p-2.5 dark:border-white/10">
          <summary className="flex cursor-pointer items-center gap-1.5 text-xs font-bold uppercase tracking-wide text-emerald-600 dark:text-emerald-400">
            <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
            Complete · {readiness.present!.length}
          </summary>
          <ul className="mt-2 space-y-1">
            {readiness.present!.map((item) => (
              <li key={item.key} className="flex items-center gap-1.5 text-xs text-slate-500 dark:text-slate-400">
                <CheckCircle2 className="h-3 w-3 shrink-0 text-emerald-500" aria-hidden="true" />
                {item.label}
              </li>
            ))}
          </ul>
        </details>
      )}

      {showDisclaimer && readiness.disclaimer && (
        <p className="border-t border-slate-100 pt-2 text-[11px] leading-relaxed text-slate-400 dark:border-white/10">
          {readiness.disclaimer}
        </p>
      )}
    </div>
  );
}
