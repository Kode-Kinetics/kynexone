'use client';

import { useCallback, useEffect, useState } from 'react';
import { Plus } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useAuth } from '../../contexts/AuthContext';
import { useFormat } from '../../hooks/useFormat';
import { describeApiError } from '../../lib/apiError';
import { msg } from '../../i18n/translations';
import { ACTION_STATUS_LABELS, pick } from '../../lib/engagementDisplay';
import { engagementApi, type Action, type ActionStatus, type Survey } from '../../api/engagement';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';
import type { ActionSeed } from './ResultsPanel';

const SOURCE_LABELS: Record<Action['sourceKind'], string> = {
  Overall: msg('Whole survey'), Driver: msg('Survey driver'), Item: msg('Question'), Cohort: msg('Group'),
};

const NEXT: Record<ActionStatus, ActionStatus[]> = {
  Open: ['InProgress', 'Done', 'Cancelled'],
  InProgress: ['Done', 'Cancelled'],
  Done: ['InProgress'],
  Cancelled: [],
};

export const statusTone = (a: Action) =>
  a.status === 'Done' ? 'emerald' : a.status === 'Cancelled' ? 'slate' : a.overdue ? 'rose' : a.status === 'InProgress' ? 'blue' : 'amber';

/**
 * Follow-up actions: what is being done about the results, by whom, by when. HR and managers see the actions of people
 * in their scope; owners record progress here or in Self-Service. Overdue owners are reminded once by the survey worker.
 */
export function ActionsPanel({ surveys, seed, onSeedConsumed }: { surveys: Survey[]; seed: ActionSeed | null; onSeedConsumed: () => void }) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const f = useFormat();
  const canManage = hasPermission('engagement.actions.manage');
  const [actions, setActions] = useState<Action[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<{ action: Action | null; seed: ActionSeed | null } | null>(null);
  const [progressing, setProgressing] = useState<Action | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setActions(await engagementApi.actions());
    } catch (e) {
      setError(describeApiError(e, t));
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    if (seed) {
      setEditing({ action: null, seed });
      onSeedConsumed();
    }
  }, [seed, onSeedConsumed]);

  return (
    <div className="space-y-3" data-testid="engagement-actions">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-slate-600 dark:text-slate-300">{t('What is being done about the results, by whom and by when.')}</p>
        {canManage && (
          <button type="button" className="btn-primary" onClick={() => setEditing({ action: null, seed: null })} data-testid="action-new">
            <Plus className="h-4 w-4" aria-hidden="true" />{t('New action')}
          </button>
        )}
      </div>
      {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-900/20 dark:text-rose-300">{error}</p>}
      {loading ? (
        <div className="h-24 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
      ) : actions.length === 0 ? (
        <div className="surface rounded-xl p-6 text-center text-sm text-slate-600 dark:text-slate-300">
          <p className="font-semibold text-slate-900 dark:text-white">{t('No actions yet')}</p>
          <p className="mt-1">{t('Create one from a driver, a question or a group on the Results tab.')}</p>
        </div>
      ) : (
        <ul className="space-y-2">
          {actions.map((a) => (
            <li key={a.id} className="surface rounded-xl p-4" data-testid={`action-${a.id}`}>
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                  <p className="font-semibold text-slate-900 dark:text-white">{a.title}</p>
                  <p className="mt-0.5 text-xs text-slate-500">
                    {t('{owner} · due {date} · {source}', { owner: a.ownerName, date: f.date(a.dueOn), source: `${t(SOURCE_LABELS[a.sourceKind])}${a.sourceRef ? `: ${a.sourceRef}` : ''}` })}
                    {a.surveyTitle && ` · ${a.surveyTitle}`}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  {a.overdue && <StatusChip label={t('Overdue')} tone="rose" dot />}
                  <StatusChip label={t(ACTION_STATUS_LABELS[a.status])} tone={statusTone(a)} />
                </div>
              </div>
              {a.description && <p className="mt-2 text-sm text-slate-700 dark:text-slate-200">{a.description}</p>}
              {a.progress.length > 1 && (
                <p className="mt-2 text-xs text-slate-500">
                  {t('Latest: {note}', { note: a.progress[a.progress.length - 1].note || t(ACTION_STATUS_LABELS[a.progress[a.progress.length - 1].status]) })}
                </p>
              )}
              {canManage && (
                <div className="mt-3 flex flex-wrap gap-2">
                  {NEXT[a.status].length > 0 && (
                    <button type="button" className="btn-secondary" onClick={() => setProgressing(a)} data-testid={`action-progress-${a.id}`}>{t('Record progress')}</button>
                  )}
                  {a.status !== 'Done' && a.status !== 'Cancelled' && (
                    <button type="button" className="btn-secondary" onClick={() => setEditing({ action: a, seed: null })}>{t('Edit')}</button>
                  )}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      <ActionEditor
        open={!!editing}
        action={editing?.action ?? null}
        seed={editing?.seed ?? null}
        surveys={surveys}
        onClose={() => setEditing(null)}
        onSaved={() => { setEditing(null); void load(); }}
        surveyLabel={(s) => pick(locale, s.titleEn, s.titleAr)}
      />
      <ProgressModal
        action={progressing}
        onClose={() => setProgressing(null)}
        submit={(a, status, note) => engagementApi.progressAction(a.id, a.version, status, note)}
        onSaved={() => { setProgressing(null); void load(); }}
      />
    </div>
  );
}

function ActionEditor({ open, action, seed, surveys, onClose, onSaved, surveyLabel }: {
  open: boolean; action: Action | null; seed: ActionSeed | null; surveys: Survey[]; onClose: () => void; onSaved: () => void;
  surveyLabel: (s: Survey) => string;
}) {
  const { t } = useLocale();
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [surveyId, setSurveyId] = useState('');
  const [sourceKind, setSourceKind] = useState<Action['sourceKind']>('Overall');
  const [sourceRef, setSourceRef] = useState('');
  const [dueOn, setDueOn] = useState('');
  const [owner, setOwner] = useState<{ id: number; name: string } | null>(null);
  const [search, setSearch] = useState('');
  const [found, setFound] = useState<{ id: number; name: string; code: string }[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setError(null);
    setTitle(action?.title ?? seed?.title ?? '');
    setDescription(action?.description ?? '');
    setSurveyId(action?.surveyId ?? seed?.surveyId ?? '');
    setSourceKind(action?.sourceKind ?? seed?.sourceKind ?? 'Overall');
    setSourceRef(action?.sourceRef ?? seed?.sourceRef ?? '');
    setDueOn(action?.dueOn ?? new Date(Date.now() + 30 * 86_400_000).toISOString().slice(0, 10));
    setOwner(action ? { id: action.ownerEmployeeId, name: action.ownerName } : null);
    setSearch('');
    setFound([]);
  }, [open, action, seed]);

  useEffect(() => {
    if (!open || search.trim().length < 2) { setFound([]); return; }
    const handle = setTimeout(() => {
      engagementApi.owners(search).then(setFound).catch(() => setFound([]));
    }, 250);
    return () => clearTimeout(handle);
  }, [open, search]);

  const save = async () => {
    if (!owner) { setError(t('Choose who owns this action.')); return; }
    setSaving(true);
    setError(null);
    const body = { surveyId: surveyId || null, sourceKind, sourceRef, title, description, ownerEmployeeId: owner.id, dueOn };
    try {
      if (action) await engagementApi.updateAction(action.id, { ...body, version: action.version });
      else await engagementApi.createAction(body);
      onSaved();
    } catch (e) {
      setError(describeApiError(e, t));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      isOpen={open}
      title={action ? t('Edit action') : t('New action')}
      onClose={onClose}
      size="lg"
      footer={(
        <>
          <button type="button" className="btn-secondary" onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className="btn-primary" onClick={() => void save()} disabled={saving || !title.trim()} data-testid="action-save">
            {saving ? t('Saving…') : t('Save action')}
          </button>
        </>
      )}
    >
      <div className="space-y-3">
        {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-900/20 dark:text-rose-300">{error}</p>}
        <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
          {t('What will be done')}
          <input className="input" value={title} onChange={(e) => setTitle(e.target.value)} data-testid="action-title" />
        </label>
        <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
          {t('Details')}
          <textarea className="input min-h-[80px]" value={description} onChange={(e) => setDescription(e.target.value)} />
        </label>
        <div className="grid gap-3 md:grid-cols-2">
          <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
            {t('Survey')}
            <select className="select" value={surveyId} onChange={(e) => setSurveyId(e.target.value)}>
              <option value="">{t('Not linked to a survey')}</option>
              {surveys.filter((s) => s.status === 'Open' || s.status === 'Closed').map((s) => <option key={s.id} value={s.id}>{surveyLabel(s)}</option>)}
            </select>
          </label>
          <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
            {t('Due date')}
            <input className="input" type="date" value={dueOn} onChange={(e) => setDueOn(e.target.value)} data-testid="action-due" />
          </label>
          <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
            {t('Answers')}
            <select className="select" value={sourceKind} onChange={(e) => setSourceKind(e.target.value as Action['sourceKind'])}>
              {(Object.keys(SOURCE_LABELS) as Action['sourceKind'][]).map((k) => <option key={k} value={k}>{t(SOURCE_LABELS[k])}</option>)}
            </select>
          </label>
          <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
            {t('Which driver, question or group')}
            <input className="input" value={sourceRef} onChange={(e) => setSourceRef(e.target.value)} />
          </label>
        </div>
        <div>
          <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
            {t('Owner')}
            <input className="input" value={owner ? owner.name : search} placeholder={t('Type at least two letters of a name')}
              onChange={(e) => { setOwner(null); setSearch(e.target.value); }} data-testid="action-owner-search" />
          </label>
          {!owner && found.length > 0 && (
            <ul className="mt-1 max-h-48 overflow-auto rounded-lg border border-slate-200 dark:border-white/10" role="listbox" aria-label={t('Matching people')}>
              {found.map((p) => (
                <li key={p.id}>
                  <button type="button" role="option" aria-selected={false} className="w-full px-3 py-2 text-start text-sm hover:bg-slate-50 dark:hover:bg-white/[0.04]"
                    onClick={() => { setOwner({ id: p.id, name: p.name }); setFound([]); }}>
                    {p.name} <span className="text-xs text-slate-500">{p.code}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
          <p className="mt-1 text-xs text-slate-500">{t('You can choose people in your own team or scope.')}</p>
        </div>
      </div>
    </Modal>
  );
}

/** Status change + note. Used by HR (engagement API) and by the owner in Self-Service (their own endpoint). */
export function ProgressModal({ action, onClose, submit, onSaved }: {
  action: Action | null; onClose: () => void; submit: (a: Action, status: ActionStatus, note: string) => Promise<Action>; onSaved: () => void;
}) {
  const { t } = useLocale();
  const [status, setStatus] = useState<ActionStatus>('InProgress');
  const [note, setNote] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!action) return;
    setStatus(NEXT[action.status][0] ?? action.status);
    setNote('');
    setError(null);
  }, [action]);

  const save = async () => {
    if (!action) return;
    setSaving(true);
    setError(null);
    try {
      await submit(action, status, note);
      onSaved();
    } catch (e) {
      setError(describeApiError(e, t));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      isOpen={!!action}
      title={t('Record progress')}
      onClose={onClose}
      size="md"
      footer={(
        <>
          <button type="button" className="btn-secondary" onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className="btn-primary" onClick={() => void save()} disabled={saving} data-testid="progress-save">
            {saving ? t('Saving…') : t('Save progress')}
          </button>
        </>
      )}
    >
      {action && (
        <div className="space-y-3">
          {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-900/20 dark:text-rose-300">{error}</p>}
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{action.title}</p>
          <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
            {t('New status')}
            <select className="select" value={status} onChange={(e) => setStatus(e.target.value as ActionStatus)} data-testid="progress-status">
              {[action.status, ...NEXT[action.status]].map((s) => <option key={s} value={s}>{t(ACTION_STATUS_LABELS[s])}</option>)}
            </select>
          </label>
          <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
            {t('Progress note')}
            <textarea className="input min-h-[80px]" value={note} onChange={(e) => setNote(e.target.value)} data-testid="progress-note" />
          </label>
          {action.progress.length > 0 && (
            <ol className="space-y-1 border-s-2 border-slate-200 ps-3 text-xs text-slate-600 dark:border-white/10 dark:text-slate-300">
              {action.progress.map((p, i) => (
                <li key={i}>{t('By {by}: {status}', { by: p.byName, status: t(ACTION_STATUS_LABELS[p.status]) })}{p.note ? ` — ${p.note}` : ''}</li>
              ))}
            </ol>
          )}
        </div>
      )}
    </Modal>
  );
}
