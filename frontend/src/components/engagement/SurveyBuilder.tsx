'use client';

import { useEffect, useMemo, useState } from 'react';
import { Users } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useAuth } from '../../contexts/AuthContext';
import { describeApiError } from '../../lib/apiError';
import { msg } from '../../i18n/translations';
import { DIMENSION_LABELS, MODE_LABELS, pick } from '../../lib/engagementDisplay';
import {
  engagementApi, type CommentDimension, type EngagementOptions, type Recurrence, type Survey, type SurveyMode, type Template,
} from '../../api/engagement';
import { Modal } from '../Modal';

const RECURRENCE_LABELS: Record<Recurrence, string> = {
  None: msg('Once'), Monthly: msg('Every month'), Quarterly: msg('Every quarter'), SemiAnnual: msg('Every six months'), Annual: msg('Every year'),
};

const toLocalInput = (iso: string) => {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

/**
 * Creates or edits a DRAFT survey: template version, bilingual title, privacy mode (with the exact notice respondents
 * will read), company, audience, dates, recurrence, minimum group size and reminders. Launching is a separate step on
 * the list, so a draft can be checked first.
 */
export function SurveyBuilder({
  open, survey, templates, options, defaultK, floor, onClose, onSaved,
}: {
  open: boolean; survey: Survey | null; templates: Template[]; options: EngagementOptions | null; defaultK: number; floor: number;
  onClose: () => void; onSaved: (s: Survey) => void;
}) {
  const { t, locale } = useLocale();
  const { user } = useAuth();
  const published = useMemo(() => templates.filter((x) => x.status === 'Published'), [templates]);
  const [templateId, setTemplateId] = useState('');
  const [titleEn, setTitleEn] = useState('');
  const [titleAr, setTitleAr] = useState('');
  const [mode, setMode] = useState<SurveyMode>('Anonymous');
  const [companyId, setCompanyId] = useState<string>('');
  const [departmentIds, setDepartmentIds] = useState<string[]>([]);
  const [designationIds, setDesignationIds] = useState<string[]>([]);
  const [gradeIds, setGradeIds] = useState<string[]>([]);
  const [excludeDays, setExcludeDays] = useState(0);
  const [opensAt, setOpensAt] = useState('');
  const [closesAt, setClosesAt] = useState('');
  const [recurrence, setRecurrence] = useState<Recurrence>('None');
  const [k, setK] = useState(defaultK);
  const [reminders, setReminders] = useState('3,7');
  const [commentDimension, setCommentDimension] = useState<CommentDimension>('department');
  const [eligible, setEligible] = useState<number | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const isGroup = !!(user as { isGroupScope?: boolean } | null)?.isGroupScope;

  useEffect(() => {
    if (!open) return;
    const now = Date.now();
    setError(null);
    setEligible(null);
    setTemplateId(survey?.templateId ?? published[0]?.id ?? '');
    setTitleEn(survey?.titleEn ?? '');
    setTitleAr(survey?.titleAr ?? '');
    setMode(survey?.mode ?? 'Anonymous');
    setCompanyId(survey?.companyId ?? (isGroup ? '' : options?.companies[0]?.value ?? ''));
    setOpensAt(toLocalInput(survey?.opensAtUtc ?? new Date(now).toISOString()));
    setClosesAt(toLocalInput(survey?.closesAtUtc ?? new Date(now + 14 * 86_400_000).toISOString()));
    setRecurrence(survey?.recurrence ?? 'None');
    setK(survey?.minCohortSize ?? defaultK);
    setReminders((survey?.reminderOffsetsDays ?? [3, 7]).join(','));
    setCommentDimension(survey?.commentDimension ?? 'department');
    setDepartmentIds([]);
    setDesignationIds([]);
    setGradeIds([]);
    setExcludeDays(0);
    if (survey) {
      void engagementApi.survey(survey.id).then((d) => {
        setDepartmentIds(d.audience.departmentIds ?? []);
        setDesignationIds(d.audience.designationIds ?? []);
        setGradeIds(d.audience.gradeIds ?? []);
        setExcludeDays(d.audience.excludeJoinedWithinDays ?? 0);
      }).catch(() => undefined);
    }
  }, [open, survey, published, defaultK, isGroup, options]);

  const notice = options?.modes.find((m) => m.value === mode);
  const audience = { departmentIds, designationIds, gradeIds, excludeJoinedWithinDays: excludeDays };
  const reminderDays = reminders.split(/[,\s]+/).map(Number).filter((n) => Number.isFinite(n) && n > 0);

  const preview = async () => {
    try {
      const r = await engagementApi.audiencePreview(companyId || null, audience, new Date(opensAt).toISOString());
      setEligible(r.eligible);
    } catch (e) {
      setError(describeApiError(e, t));
    }
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    const body = {
      templateId, titleEn, titleAr, mode, companyId: companyId || null, audience,
      opensAtUtc: new Date(opensAt).toISOString(), closesAtUtc: new Date(closesAt).toISOString(),
      recurrence, minCohortSize: k, reminderOffsetsDays: reminderDays, commentDimension,
    };
    try {
      onSaved(survey ? await engagementApi.updateSurvey(survey.id, { ...body, version: survey.version }) : await engagementApi.createSurvey(body));
    } catch (e) {
      setError(describeApiError(e, t));
    } finally {
      setSaving(false);
    }
  };

  const toggle = (list: string[], set: (v: string[]) => void, value: string) =>
    set(list.includes(value) ? list.filter((v) => v !== value) : [...list, value]);

  return (
    <Modal
      isOpen={open}
      title={survey ? t('Edit survey draft') : t('New survey')}
      onClose={onClose}
      size="xl"
      footer={(
        <>
          <button type="button" className="btn-secondary" onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className="btn-primary" onClick={() => void save()} disabled={saving || !templateId} data-testid="survey-save">
            {saving ? t('Saving…') : t('Save draft')}
          </button>
        </>
      )}
    >
      <div className="space-y-5">
        {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-900/20 dark:text-rose-300">{error}</p>}
        {published.length === 0 && (
          <p className="rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-900/20 dark:text-amber-200">
            {t('Publish a template first. A survey asks the questions of one published template version.')}
          </p>
        )}
        <div className="grid gap-3 md:grid-cols-2">
          <Field label={t('Template')}>
            <select className="select" value={templateId} onChange={(e) => setTemplateId(e.target.value)} data-testid="survey-template">
              {published.map((x) => <option key={x.id} value={x.id}>{pick(locale, x.nameEn, x.nameAr)} · {t('Version {n}', { n: x.versionNumber })}</option>)}
            </select>
          </Field>
          <Field label={t('Company')}>
            <select className="select" value={companyId} onChange={(e) => setCompanyId(e.target.value)} data-testid="survey-company">
              {isGroup && <option value="">{t('Every company')}</option>}
              {(options?.companies ?? []).map((c) => <option key={c.value} value={c.value}>{pick(locale, c.labelEn, c.labelAr)}</option>)}
            </select>
          </Field>
          <Field label={t('Title (English)')}><input className="input" value={titleEn} onChange={(e) => setTitleEn(e.target.value)} data-testid="survey-title-en" /></Field>
          <Field label={t('Title (Arabic)')}><input className="input" dir="rtl" value={titleAr} onChange={(e) => setTitleAr(e.target.value)} data-testid="survey-title-ar" /></Field>
        </div>

        <fieldset>
          <legend className="text-sm font-semibold text-slate-900 dark:text-white">{t('Privacy')}</legend>
          <div className="mt-2 grid gap-2 md:grid-cols-3">
            {(['Anonymous', 'Confidential', 'Identified'] as SurveyMode[]).map((m) => (
              <label key={m} className={`flex cursor-pointer items-center gap-2 rounded-lg border p-3 text-sm ${mode === m ? 'border-sapphire bg-sapphire/5' : 'border-slate-200 dark:border-white/10'}`}>
                <input type="radio" name="survey-mode" value={m} checked={mode === m} onChange={() => setMode(m)} data-testid={`survey-mode-${m}`} />
                {t(MODE_LABELS[m])}
              </label>
            ))}
          </div>
          {notice && (
            <p className="mt-2 rounded-lg bg-slate-50 p-3 text-xs text-slate-700 dark:bg-white/[0.04] dark:text-slate-200" data-testid="survey-mode-notice">
              <span className="block font-semibold">{t('What respondents will read')}</span>
              {locale === 'ar' ? notice.noticeAr : notice.noticeEn}
            </p>
          )}
        </fieldset>

        <fieldset className="space-y-3">
          <legend className="text-sm font-semibold text-slate-900 dark:text-white">{t('Audience')}</legend>
          <p className="text-xs text-slate-500">{t('Leave a list empty to include everyone. Only active employees are invited.')}</p>
          <ChoiceList label={t('Departments')} items={options?.departments ?? []} selected={departmentIds} onToggle={(v) => toggle(departmentIds, setDepartmentIds, v)} testId="survey-departments" />
          <ChoiceList label={t('Designations')} items={options?.designations ?? []} selected={designationIds} onToggle={(v) => toggle(designationIds, setDesignationIds, v)} />
          <ChoiceList label={t('Grades')} items={options?.grades ?? []} selected={gradeIds} onToggle={(v) => toggle(gradeIds, setGradeIds, v)} />
          <div className="grid gap-3 md:grid-cols-2">
            <Field label={t('Leave out people who joined in the last (days)')}>
              <input className="input" type="number" min={0} max={3650} value={excludeDays} onChange={(e) => setExcludeDays(Number(e.target.value) || 0)} />
            </Field>
            <div className="flex items-end gap-2">
              <button type="button" className="btn-secondary" onClick={() => void preview()} data-testid="survey-preview">
                <Users className="h-4 w-4" aria-hidden="true" />{t('Count who will be invited')}
              </button>
              {eligible !== null && <span className="text-sm font-semibold" data-testid="survey-eligible">{t('{n} people', { n: eligible })}</span>}
            </div>
          </div>
        </fieldset>

        <div className="grid gap-3 md:grid-cols-3">
          <Field label={t('Opens')}><input className="input" type="datetime-local" value={opensAt} onChange={(e) => setOpensAt(e.target.value)} /></Field>
          <Field label={t('Closes')}><input className="input" type="datetime-local" value={closesAt} onChange={(e) => setClosesAt(e.target.value)} /></Field>
          <Field label={t('Repeats')}>
            <select className="select" value={recurrence} onChange={(e) => setRecurrence(e.target.value as Recurrence)}>
              {(Object.keys(RECURRENCE_LABELS) as Recurrence[]).map((r) => <option key={r} value={r}>{t(RECURRENCE_LABELS[r])}</option>)}
            </select>
          </Field>
          <Field label={t('Smallest group shown (minimum {floor})', { floor })}>
            <input className="input" type="number" min={floor} max={100} value={k} onChange={(e) => setK(Math.max(floor, Number(e.target.value) || floor))} data-testid="survey-k" />
          </Field>
          <Field label={t('Comments can be read by')}>
            <select className="select" value={commentDimension} onChange={(e) => setCommentDimension(e.target.value as CommentDimension)} data-testid="survey-comment-dimension">
              <option value="none">{t('Whole survey only')}</option>
              {(['department', 'designation', 'grade', 'tenure', 'company'] as const).map((d) => (
                <option key={d} value={d}>{t('Whole survey or by {dimension}', { dimension: t(DIMENSION_LABELS[d]) })}</option>
              ))}
            </select>
          </Field>
          <Field label={t('Remind on days after opening')}>
            <input className="input" value={reminders} onChange={(e) => setReminders(e.target.value)} placeholder={t('For example 3, 7')} />
          </Field>
        </div>
      </div>
    </Modal>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
      {label}
      {children}
    </label>
  );
}

function ChoiceList({ label, items, selected, onToggle, testId }: {
  label: string; items: { value: string; labelEn: string; labelAr: string }[]; selected: string[]; onToggle: (v: string) => void; testId?: string;
}) {
  const { locale } = useLocale();
  if (items.length === 0) return null;
  return (
    <div>
      <p className="mb-1 text-xs font-semibold text-slate-600 dark:text-slate-300">{label}</p>
      <div className="flex flex-wrap gap-2" data-testid={testId}>
        {items.map((i) => (
          <label key={i.value} className={`flex cursor-pointer items-center gap-1.5 rounded-full border px-3 py-1 text-xs ${selected.includes(i.value) ? 'border-sapphire bg-sapphire/10 text-sapphire' : 'border-slate-200 dark:border-white/10'}`}>
            <input type="checkbox" className="sr-only" checked={selected.includes(i.value)} onChange={() => onToggle(i.value)} />
            {pick(locale, i.labelEn, i.labelAr)}
          </label>
        ))}
      </div>
    </div>
  );
}
