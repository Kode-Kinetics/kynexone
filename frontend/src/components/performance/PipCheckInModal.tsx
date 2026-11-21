'use client';

import { useCallback, useEffect, useState } from 'react';
import { Modal } from '../Modal';
import { Field, Input, Select, Textarea } from '../ui/Field';
import { Badge } from '../ui/Badge';
import { Skeleton } from '../ui/Skeleton';
import { ResultBanner } from '../ui/ResultBanner';
import { LoadFailedNotice } from '../ui/LoadFailedRow';
import { useT } from '../../hooks/useT';
import { useLocale } from '../../contexts/LocaleContext';
import { pipApi } from '../../api/performance';
import type { PIPCheckIn } from '../../api/performance';
import { formatCalendarDate } from '../../lib/calendarDate';
import { appraisalRefusal } from '../../lib/appraisalsRefusal';
import { PIP_OUTCOMES } from '../../lib/appraisalsUi';

const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};
const outcomeTone = (o: string) => (o === 'Improved' || o === 'OnTrack' ? 'success' : o === 'AtRisk' ? 'warning' : 'danger') as 'success' | 'warning' | 'danger';

/**
 * A PIP check-in: the record that the plan is being followed up. It is also what makes an adverse close possible, because the
 * API refuses to close a plan as Failed or with a termination recommendation while it has none. The meetings so far are listed
 * above the form so the new entry is written with the history in front of the manager.
 */
export function PipCheckInModal({ pipId, name, onClose, onSaved, afterSave }: {
  pipId: string; name: string; onClose: () => void; onSaved: () => void;
  /** An optional next step offered once a check-in exists, such as closing the plan. */
  afterSave?: { label: string; onClick: () => void };
}) {
  const t = useT();
  const { locale } = useLocale();
  const [history, setHistory] = useState<PIPCheckIn[] | null>(null);
  const [failure, setFailure] = useState<unknown>(null);
  const [form, setForm] = useState({ date: today(), outcome: 'OnTrack', notes: '' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  const load = useCallback(() => {
    setFailure(null);
    return pipApi.get(pipId).then(r => setHistory(r.checkIns)).catch(e => setFailure(e ?? new Error('failed')));
  }, [pipId]);
  useEffect(() => { void load(); }, [load]);

  const save = async () => {
    if (!form.date) { setError(t('apr.checkin.dateRequired')); return; }
    if (!form.notes.trim()) { setError(t('apr.checkin.notesRequired')); return; }
    setSaving(true); setError('');
    try {
      await pipApi.addCheckIn(pipId, { checkInDate: form.date, notes: form.notes.trim(), outcome: form.outcome });
      setSaved(true); setForm(f => ({ ...f, notes: '' }));
      onSaved(); await load();
    } catch (e) { setError(appraisalRefusal(e, t, t('apr.checkin.failed'), locale)); }
    finally { setSaving(false); }
  };

  return (
    <Modal isOpen title={t('apr.checkin.title', { name })} onClose={onClose} closeLabel={t('Close')} size="lg"
      footer={(
        <div className="flex justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onClose}>{t('Close')}</button>
          <button type="button" className="btn-primary" disabled={saving} aria-busy={saving || undefined} onClick={save}>{saving ? t('Saving…') : t('apr.checkin.save')}</button>
        </div>
      )}>
      <div className="space-y-5" data-testid="pip-checkin">
        <p className="text-body text-fg-muted" data-purpose>{t('apr.checkin.purpose')}</p>
        {saved && (
          <ResultBanner changed={t('apr.checkin.saved')} where={t('apr.checkin.where', { name })}
            next={afterSave} onDismiss={() => setSaved(false)} />
        )}
        <section aria-labelledby="checkin-history">
          <h3 id="checkin-history" className="mb-2 text-h4 text-fg">{t('apr.checkin.history')}</h3>
          {failure != null ? <LoadFailedNotice error={failure} onRetry={() => void load()} />
            : history === null ? <div role="status" aria-busy="true"><span className="sr-only">{t('Loading…')}</span><Skeleton className="h-10 w-full" /></div>
            : history.length === 0 ? <p className="text-body text-fg-muted" data-testid="checkin-none">{t('apr.checkin.none')}</p>
            : (
              <ul className="divide-y divide-line rounded-lg border border-line">
                {history.map(c => (
                  <li key={c.id} className="space-y-1 px-3 py-2">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-body font-medium text-fg">{formatCalendarDate(c.checkInDate, locale)}</span>
                      <Badge tone={outcomeTone(c.outcome)}>{t(`apr.outcome.${c.outcome}`)}</Badge>
                      <span className="text-meta text-fg-muted">{c.checkedByName}</span>
                    </div>
                    <p className="whitespace-pre-wrap text-body text-fg-muted">{c.notes}</p>
                  </li>
                ))}
              </ul>
            )}
        </section>
        <section className="space-y-4" aria-labelledby="checkin-new">
          <h3 id="checkin-new" className="text-h4 text-fg">{t('apr.checkin.new')}</h3>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t('Date')} required noInfo="a plain date"><Input type="date" value={form.date} onChange={e => set('date', e.target.value)} /></Field>
            <Field label={t('apr.checkin.outcome')} required info={t('apr.checkin.outcomeInfo')}>
              <Select value={form.outcome} onChange={e => set('outcome', e.target.value)}>
                {PIP_OUTCOMES.map(o => <option key={o} value={o}>{t(`apr.outcome.${o}`)}</option>)}
              </Select>
            </Field>
          </div>
          <Field label={t('apr.checkin.notes')} required noInfo="free text">
            <Textarea rows={3} value={form.notes} onChange={e => set('notes', e.target.value)} placeholder={t('apr.checkin.notesPlaceholder')} />
          </Field>
          {error && <p role="alert" className="text-body text-danger-fg">{error}</p>}
        </section>
      </div>
    </Modal>
  );
}
