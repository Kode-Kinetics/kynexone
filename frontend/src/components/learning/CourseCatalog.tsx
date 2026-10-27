'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import {
  learningApi, type CompletionRule, type ContentKind, type CourseDetail, type CourseInput, type CourseSummary, type MoveOn, type QuestionKind,
} from '../../api/learning';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import {
  completionRuleLabel, contentKindLabel, courseStatusLabel, learningError, moveOnLabel, moveOnNeedsPassMark, questionKindLabel, questionProblem, validCode,
  validHttpUrl,
} from '../../lib/learning';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import { EmptyBlock, ErrorBlock, Field, LoadingRows } from './LearningParts';

const statusTone = { Draft: 'amber', Published: 'emerald', Retired: 'slate' } as const;

/**
 * The course catalogue (LRN-01). One row per course code, showing its newest version; a published version is
 * immutable, so "Edit" on it creates the next draft version. Publishing retires the previous version — people already
 * enrolled carry on with the version they started.
 */
export function CourseCatalog({ canManage }: { canManage: boolean }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [courses, setCourses] = useState<CourseSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<CourseDetail | 'new' | null>(null);
  const [viewing, setViewing] = useState<CourseDetail | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try { setCourses(await learningApi.courses()); }
    catch (e) { setError(learningError(e, t)); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);

  // Newest version per code first; older versions are listed in the course's detail.
  const latest = useMemo(() => {
    const byCode = new Map<string, CourseSummary>();
    for (const c of courses ?? []) if (!byCode.has(c.code) || byCode.get(c.code)!.versionNumber < c.versionNumber) byCode.set(c.code, c);
    return [...byCode.values()].sort((a, b) => a.code.localeCompare(b.code));
  }, [courses]);

  const act = async (key: string, work: () => Promise<unknown>, success: string) => {
    setBusy(key);
    try {
      await work();
      toast.success(success);
      await load();
    } catch (e) {
      toast.error(learningError(e, t));
    } finally {
      setBusy(null);
    }
  };

  const openEditor = (id: string) => act(`edit-${id}`, async () => setEditing(await learningApi.course(id)), t('Draft opened.'));
  const newVersion = (id: string) => act(`version-${id}`, async () => setEditing(await learningApi.newVersion(id)), t('A new draft version was created from the published one.'));
  const view = async (id: string) => {
    try { setViewing(await learningApi.course(id)); } catch (e) { toast.error(learningError(e, t)); }
  };

  if (error) return <ErrorBlock message={error} onRetry={() => void load()} />;
  if (!courses) return <LoadingRows label={t('Loading courses')} />;

  return (
    <div className="space-y-3" data-testid="course-catalog">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('A published version never changes. To change a course, create a new version, edit it and publish it.')}</p>
        {canManage && (
          <button type="button" className="btn-primary inline-flex items-center gap-1.5 text-sm" onClick={() => setEditing('new')}>
            <Plus className="h-4 w-4" aria-hidden="true" />{t('New course')}
          </button>
        )}
      </div>
      {latest.length === 0 ? (
        <EmptyBlock title={t('No courses yet')} body={canManage ? t('Create a course: a lesson, a link to a video or document, or a cmi5 learning unit.') : t('HR has not published any courses yet.')} />
      ) : (
        <div className="wg-card overflow-x-auto rounded-2xl">
          <table className="w-full min-w-[760px] text-sm">
            <thead className="text-xs text-slate-500 dark:text-slate-400">
              <tr>
                <th scope="col" className="px-4 py-2 text-start font-semibold">{t('Course')}</th>
                <th scope="col" className="px-4 py-2 text-start font-semibold">{t('Content')}</th>
                <th scope="col" className="px-4 py-2 text-start font-semibold">{t('Completed by')}</th>
                <th scope="col" className="px-4 py-2 text-start font-semibold">{t('Status')}</th>
                <th scope="col" className="px-4 py-2 text-end font-semibold">{t('Actions')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/5">
              {latest.map((c) => (
                <tr key={c.id} data-testid={`course-row-${c.code}`}>
                  <td className="px-4 py-2.5">
                    <p className="font-semibold text-slate-800 dark:text-slate-100">{locale === 'ar' && c.titleAr ? c.titleAr : c.title}</p>
                    <p className="text-xs text-slate-500"><span dir="ltr">{c.code}</span> · {t('Version {n}', { n: c.versionNumber })}{c.publishedAtUtc ? ` · ${t('Published {date}', { date: fx.date(c.publishedAtUtc) })}` : ''}</p>
                  </td>
                  <td className="px-4 py-2.5 text-xs">{t(contentKindLabel[c.contentKind])}</td>
                  <td className="px-4 py-2.5 text-xs">
                    {t(completionRuleLabel[c.completionRule])}
                    {c.questionCount > 0 && <span className="block text-slate-500">{t('{count, plural, one {# question} other {# questions}}', { count: c.questionCount })}</span>}
                    {c.validityMonths && <span className="block text-slate-500">{t('Certificate valid {count, plural, one {# month} other {# months}}', { count: c.validityMonths })}</span>}
                  </td>
                  <td className="px-4 py-2.5"><StatusChip label={t(courseStatusLabel[c.status])} tone={statusTone[c.status]} dot /></td>
                  <td className="px-4 py-2.5">
                    <div className="flex flex-wrap justify-end gap-1.5">
                      <button type="button" className="btn-secondary text-xs" onClick={() => void view(c.id)}>{t('View')}</button>
                      {canManage && c.status === 'Draft' && (
                        <>
                          <button type="button" className="btn-secondary text-xs" disabled={busy !== null} onClick={() => void openEditor(c.id)}>{t('Edit draft')}</button>
                          <button type="button" className="btn-primary text-xs" disabled={busy !== null}
                            onClick={() => void act(`publish-${c.id}`, () => learningApi.publishCourse(c.id, c.version), t('Published. New assignments use this version.'))}>
                            {busy === `publish-${c.id}` ? t('Publishing…') : t('Publish')}
                          </button>
                        </>
                      )}
                      {canManage && c.status !== 'Draft' && (
                        <button type="button" className="btn-secondary text-xs" disabled={busy !== null} onClick={() => void newVersion(c.id)}>{t('New version')}</button>
                      )}
                      {canManage && c.status === 'Published' && (
                        <button type="button" className="btn-secondary text-xs" disabled={busy !== null}
                          onClick={() => void act(`retire-${c.id}`, () => learningApi.retireCourse(c.id), t('Retired. It can no longer be assigned; people already enrolled carry on.'))}>
                          {t('Retire')}
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {editing && (
        <CourseEditor
          course={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={async () => { setEditing(null); await load(); }}
        />
      )}
      {viewing && <CourseDetailModal course={viewing} onClose={() => setViewing(null)} />}
    </div>
  );
}

function CourseDetailModal({ course, onClose }: { course: CourseDetail; onClose: () => void }) {
  const { t, locale } = useLocale();
  return (
    <Modal isOpen title={locale === 'ar' && course.titleAr ? course.titleAr : course.title} onClose={onClose} size="lg">
      <div className="space-y-3 text-sm">
        <p className="text-xs text-slate-500">{t('{code} · version {n} · {status}', { code: course.code, n: course.versionNumber, status: t(courseStatusLabel[course.status]) })}</p>
        {course.description && <p>{course.description}</p>}
        <p><span className="font-semibold">{t('Content')}</span>{' — '}{t(contentKindLabel[course.contentKind])}</p>
        {course.lessonHtml && (
          <div dir="auto" className="rounded-xl border border-slate-200 p-3 text-sm dark:border-white/10 [&_li]:ms-5 [&_ol]:list-decimal [&_ul]:list-disc"
            // Server-sanitised lesson markup (LearningHtmlSanitizer).
            dangerouslySetInnerHTML={{ __html: course.lessonHtml }} />
        )}
        {course.linkUrl && <p className="break-all" dir="ltr">{course.linkUrl}</p>}
        {course.cmi5AuUrl && <p className="break-all text-xs" dir="ltr">{course.cmi5AuUrl} · {course.cmi5ActivityId}</p>}
        <p><span className="font-semibold">{t('Completed by')}</span>{' — '}{t(completionRuleLabel[course.completionRule])}</p>
        {course.questions && (
          <ol className="list-decimal space-y-1 ps-5 text-xs">
            {course.questions.map((q) => (
              <li key={q.id}>{q.prompt} <span className="text-slate-500">({t(questionKindLabel[q.kind])})</span></li>
            ))}
          </ol>
        )}
        <h3 className="pt-2 text-xs font-semibold text-slate-500">{t('Versions')}</h3>
        <ul className="text-xs">
          {course.versions.map((v) => (
            <li key={v.id}>{t('Version {n}: {status}', { n: v.versionNumber, status: t(courseStatusLabel[v.status]) })}</li>
          ))}
        </ul>
      </div>
    </Modal>
  );
}

interface QuestionDraft { kind: QuestionKind; prompt: string; options: string[]; correct: number[]; trueFalse: boolean | null }

const emptyQuestion = (): QuestionDraft => ({ kind: 'Single', prompt: '', options: ['', ''], correct: [], trueFalse: null });

function toDrafts(course: CourseDetail | null): QuestionDraft[] {
  return (course?.questions ?? []).map((q) => ({
    kind: q.kind,
    prompt: q.prompt,
    options: q.kind === 'TrueFalse' ? ['', ''] : q.options.map((o) => o.text),
    correct: q.kind === 'TrueFalse' ? [] : q.options.map((o, i) => (q.correctOptionIds.includes(o.id) ? i : -1)).filter((i) => i >= 0),
    trueFalse: q.kind === 'TrueFalse' ? q.correctOptionIds.includes('true') : null,
  }));
}

/** Create a course, or edit a draft version. Labels persist, errors sit under their field, the server re-validates. */
function CourseEditor({ course, onClose, onSaved }: { course: CourseDetail | null; onClose: () => void; onSaved: () => Promise<void> }) {
  const { t } = useLocale();
  const toast = useAppToast();
  const [code, setCode] = useState(course?.code ?? '');
  const [title, setTitle] = useState(course?.title ?? '');
  const [titleAr, setTitleAr] = useState(course?.titleAr ?? '');
  const [description, setDescription] = useState(course?.description ?? '');
  const [minutes, setMinutes] = useState(course?.estimatedMinutes?.toString() ?? '');
  const [kind, setKind] = useState<ContentKind>(course?.contentKind ?? 'Lesson');
  const [lessonHtml, setLessonHtml] = useState(course?.lessonHtml ?? '');
  const [linkUrl, setLinkUrl] = useState(course?.linkUrl ?? '');
  const [auUrl, setAuUrl] = useState(course?.cmi5AuUrl ?? '');
  const [activityId, setActivityId] = useState(course?.cmi5ActivityId ?? '');
  const [moveOn, setMoveOn] = useState<MoveOn>(course?.cmi5MoveOn ?? 'Completed');
  const [rule, setRule] = useState<CompletionRule>(course?.completionRule ?? 'Acknowledgement');
  const [passMark, setPassMark] = useState(course?.passMarkPercent?.toString() ?? '70');
  const [maxAttempts, setMaxAttempts] = useState(course?.maxAttempts?.toString() ?? '3');
  const [shuffle, setShuffle] = useState(course?.shuffleQuestions ?? false);
  const [validity, setValidity] = useState(course?.validityMonths?.toString() ?? '');
  const [certificate, setCertificate] = useState(course?.issuesCertificate ?? true);
  // New cmi5 units default to HR verification: what the unit reports comes from the learner's browser.
  const [verification, setVerification] = useState(course?.requiresHrVerification ?? true);
  const [questions, setQuestions] = useState<QuestionDraft[]>(() => (course?.questions?.length ? toDrafts(course) : [emptyQuestion()]));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState<'save' | 'publish' | null>(null);

  const effectiveRule: CompletionRule = kind === 'Cmi5' ? 'Cmi5MoveOn' : rule === 'Cmi5MoveOn' ? 'Acknowledgement' : rule;
  const unitPassMark = kind === 'Cmi5' && moveOnNeedsPassMark(moveOn);
  const grantsCredential = certificate || validity.trim() !== '';

  const validate = (): Record<string, string> => {
    const e: Record<string, string> = {};
    if (!course && !validCode(code)) e.code = t('Use 2–40 letters, digits or dashes, e.g. FIRE-SAFETY.');
    if (!title.trim()) e.title = t('Give the course a title.');
    if (kind === 'Lesson' && !lessonHtml.trim()) e.lesson = t('Write the lesson.');
    if (kind === 'Link' && !validHttpUrl(linkUrl)) e.link = t('Enter a full address starting with https://');
    if (kind === 'Cmi5' && !validHttpUrl(auUrl)) e.au = t('Enter the unit’s full address starting with https://');
    if (kind === 'Cmi5' && !validHttpUrl(activityId)) e.activity = t('Enter the unit’s activity id, a full address such as https://example.com/units/fire.');
    if (validity && (!/^\d+$/.test(validity) || Number(validity) < 1 || Number(validity) > 120)) e.validity = t('Between 1 and 120 months, or empty for no expiry.');
    if (kind === 'Cmi5' && moveOn === 'NotApplicable' && grantsCredential)
      e.moveOn = t('A unit that counts as complete when opened cannot issue a certificate or expire. Choose another rule, or turn the certificate off.');
    if (effectiveRule === 'Assessment' || unitPassMark) {
      const mark = Number(passMark);
      if (!passMark || Number.isNaN(mark) || mark <= 0 || mark > 100) e.passMark = t('A pass mark above 0 and up to 100.');
    }
    if (effectiveRule === 'Assessment') {
      const attempts = Number(maxAttempts);
      if (!Number.isInteger(attempts) || attempts < 1 || attempts > 20) e.attempts = t('Between 1 and 20 attempts.');
      questions.forEach((q, i) => { const p = questionProblem(q); if (p) e[`q${i}`] = t(p); });
      if (questions.length === 0) e.questions = t('Add at least one question.');
    }
    return e;
  };

  const body = (): CourseInput => ({
    code: code.trim().toUpperCase(), title: title.trim(), titleAr: titleAr.trim() || null, description: description.trim() || null,
    estimatedMinutes: minutes ? Number(minutes) : null, contentKind: kind,
    lessonHtml: kind === 'Lesson' ? lessonHtml : null, linkUrl: kind === 'Link' ? linkUrl.trim() : null,
    cmi5AuUrl: kind === 'Cmi5' ? auUrl.trim() : null, cmi5ActivityId: kind === 'Cmi5' ? activityId.trim() : null, cmi5MoveOn: kind === 'Cmi5' ? moveOn : null,
    completionRule: effectiveRule,
    questions: effectiveRule === 'Assessment' ? questions.map((q) => ({
      kind: q.kind, prompt: q.prompt.trim(),
      options: q.kind === 'TrueFalse' ? [] : q.options.map((text) => ({ text: text.trim() })),
      correctOptionIndexes: q.kind === 'TrueFalse' ? [] : q.correct,
      trueFalseAnswer: q.kind === 'TrueFalse' ? q.trueFalse : null,
    })) : null,
    passMarkPercent: effectiveRule === 'Assessment' || unitPassMark ? Number(passMark) : null,
    maxAttempts: effectiveRule === 'Assessment' ? Number(maxAttempts) : null,
    shuffleQuestions: shuffle, validityMonths: validity ? Number(validity) : null, issuesCertificate: certificate,
    requiresHrVerification: kind === 'Cmi5' ? verification : null,
    version: course?.version,
  });

  const save = async (publish: boolean) => {
    const problems = validate();
    setErrors(problems);
    if (Object.keys(problems).length > 0) return;
    setSaving(publish ? 'publish' : 'save');
    try {
      const saved = course ? await learningApi.updateCourse(course.id, body()) : await learningApi.createCourse(body());
      if (publish) await learningApi.publishCourse(saved.id, saved.version);
      toast.success(publish ? t('Published. New assignments use this version.') : t('Draft saved.'));
      await onSaved();
    } catch (e) {
      toast.error(learningError(e, t));
    } finally {
      setSaving(null);
    }
  };

  const updateQuestion = (index: number, patch: Partial<QuestionDraft>) =>
    setQuestions((qs) => qs.map((q, i) => (i === index ? { ...q, ...patch } : q)));

  const inputClass = (key: string) => `input w-full ${errors[key] ? 'border-rose-400' : ''}`;
  const described = (key: string) => (errors[key] ? `lc-${key}-error` : undefined);

  return (
    <Modal isOpen size="xl" onClose={onClose}
      title={course ? t('Edit {code}, version {n} (draft)', { code: course.code, n: course.versionNumber }) : t('New course')}
      footer={(
        <div className="flex flex-wrap justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onClose} disabled={saving !== null}>{t('Cancel')}</button>
          <button type="button" className="btn-secondary" onClick={() => void save(false)} disabled={saving !== null} data-testid="course-save-draft">
            {saving === 'save' ? t('Saving...') : t('Save draft')}
          </button>
          <button type="button" className="btn-primary" onClick={() => void save(true)} disabled={saving !== null} data-testid="course-publish">
            {saving === 'publish' ? t('Publishing…') : t('Save and publish')}
          </button>
        </div>
      )}>
      <form className="space-y-5" onSubmit={(e) => { e.preventDefault(); void save(false); }} noValidate data-testid="course-editor">
        <fieldset className="grid gap-3 sm:grid-cols-2">
          <legend className="mb-2 text-sm font-semibold">{t('About the course')}</legend>
          <Field id="lc-code" label={t('Code')} hint={t('Stays the same across versions.')} error={errors.code}>
            <input id="lc-code" className={inputClass('code')} value={code} onChange={(e) => setCode(e.target.value)} disabled={!!course} dir="ltr" aria-describedby={described('code')} maxLength={40} />
          </Field>
          <Field id="lc-minutes" label={t('Time to complete (minutes)')} optional>
            <input id="lc-minutes" type="number" min={1} className="input w-full" value={minutes} onChange={(e) => setMinutes(e.target.value)} />
          </Field>
          <Field id="lc-title" label={t('Title')} error={errors.title}>
            <input id="lc-title" className={inputClass('title')} value={title} onChange={(e) => setTitle(e.target.value)} aria-describedby={described('title')} maxLength={200} />
          </Field>
          <Field id="lc-title-ar" label={t('Title in Arabic')} optional>
            <input id="lc-title-ar" className="input w-full" dir="rtl" value={titleAr} onChange={(e) => setTitleAr(e.target.value)} maxLength={200} />
          </Field>
          <div className="sm:col-span-2">
            <Field id="lc-description" label={t('Description')} optional>
              <textarea id="lc-description" className="input w-full" rows={2} value={description} onChange={(e) => setDescription(e.target.value)} maxLength={2000} />
            </Field>
          </div>
        </fieldset>

        <fieldset className="space-y-3">
          <legend className="mb-2 text-sm font-semibold">{t('Content')}</legend>
          <div className="flex flex-wrap gap-3" role="radiogroup" aria-label={t('Content type')}>
            {(['Lesson', 'Link', 'Cmi5'] as ContentKind[]).map((k) => (
              <label key={k} className="flex items-center gap-1.5 text-sm">
                <input type="radio" name="lc-kind" checked={kind === k} onChange={() => setKind(k)} />{t(contentKindLabel[k])}
              </label>
            ))}
          </div>
          {kind === 'Lesson' && (
            <Field id="lc-lesson" label={t('Lesson text')} hint={t('Headings, bold, italics, lists and links are kept. Anything else (scripts, styles, images, frames) is removed when you save.')} error={errors.lesson}>
              <textarea id="lc-lesson" className={`${inputClass('lesson')} font-mono text-xs`} rows={8} value={lessonHtml} onChange={(e) => setLessonHtml(e.target.value)} aria-describedby={described('lesson')} dir="auto" />
            </Field>
          )}
          {kind === 'Link' && (
            <Field id="lc-link" label={t('Address of the video or document')} error={errors.link}>
              <input id="lc-link" className={inputClass('link')} dir="ltr" value={linkUrl} onChange={(e) => setLinkUrl(e.target.value)} aria-describedby={described('link')} />
            </Field>
          )}
          {kind === 'Cmi5' && (
            <div className="grid gap-3 sm:grid-cols-2">
              <Field id="lc-au" label={t('Unit address (opens in its own window)')} error={errors.au}>
                <input id="lc-au" className={inputClass('au')} dir="ltr" value={auUrl} onChange={(e) => setAuUrl(e.target.value)} aria-describedby={described('au')} />
              </Field>
              <Field id="lc-activity" label={t('Activity id')} error={errors.activity}>
                <input id="lc-activity" className={inputClass('activity')} dir="ltr" value={activityId} onChange={(e) => setActivityId(e.target.value)} aria-describedby={described('activity')} />
              </Field>
              <Field id="lc-moveon" label={t('Counts as complete when the unit reports')} error={errors.moveOn}>
                <select id="lc-moveon" className={inputClass('moveOn')} value={moveOn} onChange={(e) => setMoveOn(e.target.value as MoveOn)} aria-describedby={described('moveOn')}>
                  {(['Completed', 'Passed', 'CompletedAndPassed', 'CompletedOrPassed', 'NotApplicable'] as MoveOn[]).map((m) => <option key={m} value={m}>{t(moveOnLabel[m])}</option>)}
                </select>
              </Field>
              {unitPassMark && (
                <Field id="lc-pass" label={t('Pass mark the unit must report (%)')} hint={t('Sent to the unit as its mastery score. A “passed” below it is refused.')} error={errors.passMark}>
                  <input id="lc-pass" type="number" min={1} max={100} className={inputClass('passMark')} value={passMark} onChange={(e) => setPassMark(e.target.value)} aria-describedby={described('passMark')} />
                </Field>
              )}
            </div>
          )}
        </fieldset>

        <fieldset className="space-y-3">
          <legend className="mb-2 text-sm font-semibold">{t('How it is completed')}</legend>
          {kind === 'Cmi5' ? (
            <p className="text-xs text-slate-600 dark:text-slate-300">{t('A cmi5 unit is completed only by what the unit itself reports.')}</p>
          ) : (
            <div className="flex flex-wrap gap-3" role="radiogroup" aria-label={t('How it is completed')}>
              {(['Acknowledgement', 'Assessment'] as CompletionRule[]).map((r) => (
                <label key={r} className="flex items-center gap-1.5 text-sm">
                  <input type="radio" name="lc-rule" checked={effectiveRule === r} onChange={() => setRule(r)} />{t(completionRuleLabel[r])}
                </label>
              ))}
            </div>
          )}
          {effectiveRule === 'Assessment' && (
            <div className="space-y-3">
              <div className="grid gap-3 sm:grid-cols-3">
                <Field id="lc-pass" label={t('Pass mark (%)')} error={errors.passMark}>
                  <input id="lc-pass" type="number" min={1} max={100} className={inputClass('passMark')} value={passMark} onChange={(e) => setPassMark(e.target.value)} aria-describedby={described('passMark')} />
                </Field>
                <Field id="lc-attempts" label={t('Attempts allowed')} error={errors.attempts}>
                  <input id="lc-attempts" type="number" min={1} max={20} className={inputClass('attempts')} value={maxAttempts} onChange={(e) => setMaxAttempts(e.target.value)} aria-describedby={described('attempts')} />
                </Field>
                <label className="flex items-center gap-2 self-end pb-2 text-sm">
                  <input type="checkbox" checked={shuffle} onChange={(e) => setShuffle(e.target.checked)} />{t('Shuffle the questions for each attempt')}
                </label>
              </div>
              {errors.questions && <p role="alert" className="text-xs text-rose-600">{errors.questions}</p>}
              <ol className="space-y-3">
                {questions.map((q, i) => (
                  <li key={i} className="space-y-2 rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="question-editor">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <p className="text-xs font-semibold">{t('Question {n}', { n: i + 1 })}</p>
                      <div className="flex items-center gap-2">
                        <select className="input w-auto py-1 text-xs" aria-label={t('Question type')} value={q.kind}
                          onChange={(e) => updateQuestion(i, { kind: e.target.value as QuestionKind, correct: [], trueFalse: null })}>
                          {(['Single', 'Multi', 'TrueFalse'] as QuestionKind[]).map((k) => <option key={k} value={k}>{t(questionKindLabel[k])}</option>)}
                        </select>
                        <button type="button" className="btn-secondary inline-flex items-center gap-1 text-xs" onClick={() => setQuestions((qs) => qs.filter((_, x) => x !== i))}
                          aria-label={t('Remove question {n}', { n: i + 1 })}>
                          <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />{t('Remove')}
                        </button>
                      </div>
                    </div>
                    <input className="input w-full" aria-label={t('Question {n} text', { n: i + 1 })} value={q.prompt} onChange={(e) => updateQuestion(i, { prompt: e.target.value })} />
                    {q.kind === 'TrueFalse' ? (
                      <div className="flex gap-4 text-sm" role="radiogroup" aria-label={t('Correct answer')}>
                        <label className="flex items-center gap-1.5"><input type="radio" name={`tf-${i}`} checked={q.trueFalse === true} onChange={() => updateQuestion(i, { trueFalse: true })} />{t('True')}</label>
                        <label className="flex items-center gap-1.5"><input type="radio" name={`tf-${i}`} checked={q.trueFalse === false} onChange={() => updateQuestion(i, { trueFalse: false })} />{t('False')}</label>
                      </div>
                    ) : (
                      <div className="space-y-1.5">
                        {q.options.map((o, oi) => (
                          <div key={oi} className="flex items-center gap-2">
                            <input type={q.kind === 'Single' ? 'radio' : 'checkbox'} name={`q-${i}-correct`} checked={q.correct.includes(oi)}
                              aria-label={t('Answer {n} is correct', { n: oi + 1 })}
                              onChange={(e) => updateQuestion(i, { correct: q.kind === 'Single' ? [oi] : e.target.checked ? [...q.correct, oi] : q.correct.filter((x) => x !== oi) })} />
                            <input className="input flex-1 py-1 text-sm" aria-label={t('Answer {n}', { n: oi + 1 })} value={o}
                              onChange={(e) => updateQuestion(i, { options: q.options.map((x, xi) => (xi === oi ? e.target.value : x)) })} />
                            {q.options.length > 2 && (
                              <button type="button" className="text-xs text-slate-500 hover:text-rose-600" aria-label={t('Remove answer {n}', { n: oi + 1 })}
                                onClick={() => updateQuestion(i, { options: q.options.filter((_, xi) => xi !== oi), correct: q.correct.filter((x) => x !== oi).map((x) => (x > oi ? x - 1 : x)) })}>
                                {t('Remove')}
                              </button>
                            )}
                          </div>
                        ))}
                        {q.options.length < 10 && (
                          <button type="button" className="text-xs font-semibold text-sapphire hover:underline dark:text-cyanAccent" onClick={() => updateQuestion(i, { options: [...q.options, ''] })}>
                            {t('Add an answer')}
                          </button>
                        )}
                      </div>
                    )}
                    {errors[`q${i}`] && <p role="alert" className="text-xs text-rose-600">{errors[`q${i}`]}</p>}
                  </li>
                ))}
              </ol>
              <button type="button" className="btn-secondary inline-flex items-center gap-1.5 text-xs" onClick={() => setQuestions((qs) => [...qs, emptyQuestion()])}>
                <Plus className="h-3.5 w-3.5" aria-hidden="true" />{t('Add a question')}
              </button>
            </div>
          )}
        </fieldset>

        <fieldset className="grid gap-3 sm:grid-cols-2">
          <legend className="mb-2 text-sm font-semibold">{t('Certificate')}</legend>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={certificate} onChange={(e) => setCertificate(e.target.checked)} />{t('Issue a certificate on completion')}
          </label>
          <Field id="lc-validity" label={t('Valid for (months)')} hint={t('Leave empty if it never expires. Expiring certificates are renewed automatically.')} error={errors.validity} optional>
            <input id="lc-validity" type="number" min={1} max={120} className={inputClass('validity')} value={validity} onChange={(e) => setValidity(e.target.value)} aria-describedby={described('validity')} />
          </Field>
          {kind === 'Cmi5' && (
            <div className="space-y-1 sm:col-span-2">
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={verification} onChange={(e) => setVerification(e.target.checked)} data-testid="course-hr-verification" aria-describedby="lc-verification-hint" />
                {t('HR checks the completion before the certificate is issued')}
              </label>
              <p id="lc-verification-hint" className="text-caption text-slate-500 dark:text-slate-400">
                {grantsCredential
                  ? t('Recommended. The unit reports completion from the learner’s browser, so its report alone is not proof. HR confirms it in Records.')
                  : t('Only matters when the unit issues a certificate or expires.')}
              </p>
            </div>
          )}
        </fieldset>
      </form>
    </Modal>
  );
}
