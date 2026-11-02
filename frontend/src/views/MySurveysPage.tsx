'use client';

import { useCallback, useEffect, useState } from 'react';
import { CheckCircle2, Lock, Save, Send } from 'lucide-react';
import { useLocale } from '../contexts/LocaleContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useFormat } from '../hooks/useFormat';
import { describeApiError } from '../lib/apiError';
import {
  ACTION_STATUS_LABELS, LIKERT_LABELS, MODE_LABELS, MY_SURVEY_STATUS_LABELS, fromAnswers, missingRequired, pick, progressPct, toAnswers,
  type AnswerState,
} from '../lib/engagementDisplay';
import { essSurveysApi, engagementRefusal, type Action, type MySurvey, type Question, type SurveySession } from '../api/engagement';
import { StatusChip } from '../components/StatusChip';
import { ProgressModal, statusTone } from '../components/engagement/ActionsPanel';

/**
 * Self-Service → Surveys: the employee's open surveys, the privacy notice of each (verbatim from the server, so it says
 * exactly what the system does), a draft they can come back to, one submission, and the follow-up actions they own.
 * One column and large tap targets: built for a phone first, mirrored for Arabic by logical spacing.
 */
export function MySurveysPage() {
  const { t } = useLocale();
  const { isLoading, isFeatureEnabled } = useFeatureFlags();
  if (isLoading) return <div className="h-24 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />;
  if (!isFeatureEnabled('engagement')) {
    return (
      <div className="rounded-2xl border border-slate-200 bg-white p-6 text-sm text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300">
        {t('This feature is not enabled for your account yet.')}
      </div>
    );
  }
  return <MySurveys />;
}

function MySurveys() {
  const { t, locale } = useLocale();
  const f = useFormat();
  const [surveys, setSurveys] = useState<MySurvey[]>([]);
  const [actions, setActions] = useState<Action[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [session, setSession] = useState<SurveySession | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [progressing, setProgressing] = useState<Action | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [s, a] = await Promise.all([essSurveysApi.mine(), essSurveysApi.myActions()]);
      setSurveys(s);
      setActions(a);
    } catch (e) {
      setError(engagementRefusal(e) === 'no_employee_record'
        ? t('Your login is not linked to an employee record yet. Ask HR to link it.')
        : describeApiError(e, t));
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { void load(); }, [load]);

  const open = async (surveyId: string) => {
    setError(null);
    setDone(null);
    try {
      setSession(await essSurveysApi.open(surveyId));
    } catch (e) {
      // Reload first (the survey's state changed), then say why: load() clears the previous error.
      const message = describeApiError(e, t);
      await load();
      setError(message);
    }
  };

  if (session) {
    return (
      <Respond
        session={session}
        onExit={() => { setSession(null); void load(); }}
        onSubmitted={(title) => { setSession(null); setDone(title); void load(); }}
      />
    );
  }

  const openSurveys = surveys.filter((s) => s.status === 'NotStarted' || s.status === 'Draft');
  const past = surveys.filter((s) => s.status === 'Submitted' || s.status === 'Closed');

  return (
    <div className="space-y-5" data-testid="my-surveys">
      <header>
        <h1 className="text-xl font-semibold text-slate-950 dark:text-white">{t('My surveys')}</h1>
        <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{t('Tell your organisation how work is going. Each survey says exactly who can see what before you answer.')}</p>
      </header>
      {done && (
        <p role="status" className="flex items-start gap-2 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-900 dark:bg-emerald-900/20 dark:text-emerald-100" data-testid="survey-thanks">
          <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          {t('Thank you. Your response to {title} was submitted.', { title: done })}
        </p>
      )}
      {error && <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-800/60 dark:bg-rose-900/20 dark:text-rose-300">{error}</p>}

      {loading ? (
        <div className="h-32 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
      ) : (
        <>
          <section className="space-y-2">
            <h2 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Waiting for you')}</h2>
            {openSurveys.length === 0 ? (
              <p className="surface rounded-xl p-4 text-sm text-slate-600 dark:text-slate-300">{t('No surveys are waiting for you.')}</p>
            ) : (
              <ul className="space-y-2">
                {openSurveys.map((s) => (
                  <li key={s.surveyId} className="surface rounded-xl p-4" data-testid={`my-survey-${s.surveyId}`}>
                    <div className="flex flex-wrap items-start justify-between gap-2">
                      <div className="min-w-0">
                        <p className="font-semibold text-slate-900 dark:text-white">{pick(locale, s.titleEn, s.titleAr)}</p>
                        <p className="mt-0.5 text-xs text-slate-500">{t('Closes {date}', { date: f.dateTime(s.closesAtUtc) })}</p>
                      </div>
                      <div className="flex flex-wrap gap-2">
                        <StatusChip label={t(MODE_LABELS[s.mode])} tone={s.mode === 'Identified' ? 'amber' : 'emerald'} dot />
                        <StatusChip label={t(MY_SURVEY_STATUS_LABELS[s.status])} tone={s.status === 'Draft' ? 'blue' : 'slate'} />
                      </div>
                    </div>
                    <button type="button" className="btn-primary mt-3 w-full justify-center sm:w-auto" onClick={() => void open(s.surveyId)} data-testid={`my-survey-open-${s.surveyId}`}>
                      {s.status === 'Draft' ? t('Continue') : t('Start survey')}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>

          {past.length > 0 && (
            <section className="space-y-2">
              <h2 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Earlier surveys')}</h2>
              <ul className="space-y-2">
                {past.map((s) => (
                  <li key={s.surveyId} className="surface flex flex-wrap items-center justify-between gap-2 rounded-xl p-4">
                    <span className="text-sm font-semibold text-slate-900 dark:text-white">{pick(locale, s.titleEn, s.titleAr)}</span>
                    <span className="text-xs text-slate-500">
                      {s.status === 'Submitted' && s.submittedOn
                        ? t('You responded on {date}', { date: f.date(s.submittedOn) })
                        : t(MY_SURVEY_STATUS_LABELS[s.status])}
                    </span>
                  </li>
                ))}
              </ul>
            </section>
          )}

          {actions.length > 0 && (
            <section className="space-y-2" data-testid="my-actions">
              <h2 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Actions you own')}</h2>
              <ul className="space-y-2">
                {actions.map((a) => (
                  <li key={a.id} className="surface rounded-xl p-4" data-testid={`my-action-${a.id}`}>
                    <div className="flex flex-wrap items-start justify-between gap-2">
                      <div className="min-w-0">
                        <p className="font-semibold text-slate-900 dark:text-white">{a.title}</p>
                        <p className="mt-0.5 text-xs text-slate-500">{t('Due {date}', { date: f.date(a.dueOn) })}</p>
                      </div>
                      <div className="flex gap-2">
                        {a.overdue && <StatusChip label={t('Overdue')} tone="rose" dot />}
                        <StatusChip label={t(ACTION_STATUS_LABELS[a.status])} tone={statusTone(a)} />
                      </div>
                    </div>
                    {a.description && <p className="mt-2 text-sm text-slate-700 dark:text-slate-200">{a.description}</p>}
                    {a.status !== 'Cancelled' && (
                      <button type="button" className="btn-secondary mt-3" onClick={() => setProgressing(a)} data-testid={`my-action-progress-${a.id}`}>{t('Record progress')}</button>
                    )}
                  </li>
                ))}
              </ul>
            </section>
          )}
        </>
      )}

      <ProgressModal
        action={progressing}
        onClose={() => setProgressing(null)}
        submit={(a, status, note) => essSurveysApi.progress(a.id, a.version, status, note)}
        onSaved={() => { setProgressing(null); void load(); }}
      />
    </div>
  );
}

function Respond({ session, onExit, onSubmitted }: { session: SurveySession; onExit: () => void; onSubmitted: (title: string) => void }) {
  const { t, locale } = useLocale();
  const f = useFormat();
  const [answers, setAnswers] = useState<AnswerState>(() => fromAnswers(session.draft));
  const [saving, setSaving] = useState<'draft' | 'submit' | null>(null);
  const [savedOn, setSavedOn] = useState<string | null>(session.draft.length > 0 ? 'restored' : null);
  const [error, setError] = useState<string | null>(null);
  const missing = missingRequired(session.content, answers);
  const title = pick(locale, session.titleEn, session.titleAr);

  const set = (key: string, patch: AnswerState[string]) => setAnswers((a) => ({ ...a, [key]: { ...a[key], ...patch } }));

  const saveDraft = async () => {
    setSaving('draft');
    setError(null);
    try {
      const r = await essSurveysApi.saveDraft(session.surveyId, session.token, toAnswers(answers));
      setSavedOn(r.savedOn);
    } catch (e) {
      setError(describeApiError(e, t));
    } finally {
      setSaving(null);
    }
  };

  const submit = async () => {
    setSaving('submit');
    setError(null);
    try {
      await essSurveysApi.submit(session.surveyId, session.token, toAnswers(answers));
      onSubmitted(title);
    } catch (e) {
      if (engagementRefusal(e) === 'survey_already_submitted') onSubmitted(title);
      else setError(describeApiError(e, t));
    } finally {
      setSaving(null);
    }
  };

  return (
    <form className="mx-auto max-w-2xl space-y-4" onSubmit={(e) => { e.preventDefault(); void submit(); }} data-testid="survey-respond">
      <header className="space-y-1">
        <button type="button" className="text-xs font-semibold text-sapphire hover:underline" onClick={onExit}>{t('Back to my surveys')}</button>
        <h1 className="text-xl font-semibold text-slate-950 dark:text-white">{title}</h1>
        <p className="text-xs text-slate-500">{t('Closes {date}', { date: f.dateTime(session.closesAtUtc) })}</p>
      </header>

      <section className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-950 dark:border-emerald-800/60 dark:bg-emerald-900/20 dark:text-emerald-50" data-testid="privacy-notice">
        <p className="flex items-center gap-2 font-semibold">
          <Lock className="h-4 w-4" aria-hidden="true" />
          {t('{mode} survey: who can see your answers', { mode: t(MODE_LABELS[session.mode]) })}
        </p>
        <p className="mt-1">{locale === 'ar' ? session.privacyNoticeAr : session.privacyNoticeEn}</p>
      </section>

      <div className="h-2 rounded-full bg-slate-100 dark:bg-white/[0.06]" role="progressbar" aria-valuemin={0} aria-valuemax={100}
        aria-valuenow={progressPct(session.content, answers)} aria-label={t('Survey progress')}>
        <div className="h-2 rounded-full bg-sapphire transition-all" style={{ width: `${progressPct(session.content, answers)}%` }} />
      </div>

      <ol className="space-y-4">
        {session.content.questions.map((q, i) => (
          <li key={q.key} className="surface rounded-xl p-4" data-testid={`question-${q.key}`}>
            <QuestionInput q={q} index={i} answer={answers[q.key]} onChange={(patch) => set(q.key, patch)} />
          </li>
        ))}
      </ol>

      {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-900/20 dark:text-rose-300">{error}</p>}

      <div className="flex flex-col gap-2 pb-24 sm:flex-row sm:items-center sm:justify-between sm:pb-0">
        <div className="space-y-0.5 text-xs text-slate-500" aria-live="polite">
          {savedOn && (
            <p data-testid="respond-draft-state">
              {savedOn === 'restored' ? t('Your saved draft was restored.') : t('Draft saved. Only you can see it, and it is deleted when you submit.')}
            </p>
          )}
          <p>
            {missing.length > 0
              ? t('{n, plural, one {# required question left} other {# required questions left}}', { n: missing.length })
              : t('Ready to submit.')}
          </p>
        </div>
        <div className="flex gap-2">
          <button type="button" className="btn-secondary flex-1 justify-center sm:flex-none" onClick={() => void saveDraft()} disabled={!!saving} data-testid="respond-save-draft">
            <Save className="h-4 w-4" aria-hidden="true" />{saving === 'draft' ? t('Saving…') : t('Save draft')}
          </button>
          <button type="submit" className="btn-primary flex-1 justify-center sm:flex-none" disabled={!!saving || missing.length > 0} data-testid="respond-submit">
            <Send className="h-4 w-4" aria-hidden="true" />{saving === 'submit' ? t('Submitting…') : t('Submit')}
          </button>
        </div>
      </div>
    </form>
  );
}

function QuestionInput({ q, index, answer, onChange }: {
  q: Question; index: number; answer: AnswerState[string] | undefined; onChange: (patch: AnswerState[string]) => void;
}) {
  const { t, locale } = useLocale();
  const text = pick(locale, q.textEn, q.textAr);
  const legend = (
    <legend id={`${q.key}-legend`} className="text-sm font-semibold text-slate-900 dark:text-white">
      {t('Question {n}', { n: index + 1 })}: {text}
      {q.required ? <span className="ms-1 text-rose-600" aria-hidden="true">*</span> : <span className="ms-2 text-xs font-normal text-slate-500">{t('Optional')}</span>}
    </legend>
  );

  if (q.type === 'likert5') {
    return (
      <fieldset>
        {legend}
        <div className="mt-3 grid grid-cols-1 gap-2 sm:grid-cols-5">
          {[1, 2, 3, 4, 5].map((v) => (
            <label key={v} className={`flex min-h-[44px] cursor-pointer items-center gap-2 rounded-lg border px-3 py-2 text-sm sm:flex-col sm:text-center ${answer?.value === v ? 'border-sapphire bg-sapphire/10 font-semibold text-sapphire' : 'border-slate-200 dark:border-white/10'}`}>
              <input type="radio" name={q.key} value={v} checked={answer?.value === v} onChange={() => onChange({ value: v })} required={q.required} />
              {t(LIKERT_LABELS[v - 1])}
            </label>
          ))}
        </div>
      </fieldset>
    );
  }
  if (q.type === 'enps') {
    return (
      <fieldset>
        {legend}
        <div className="mt-3 grid grid-cols-6 gap-1.5 sm:grid-cols-11">
          {Array.from({ length: 11 }, (_, v) => (
            <label key={v} className={`flex min-h-[44px] cursor-pointer items-center justify-center rounded-lg border text-sm ${answer?.value === v ? 'border-sapphire bg-sapphire text-white' : 'border-slate-200 dark:border-white/10'}`}>
              <input type="radio" className="sr-only" name={q.key} value={v} checked={answer?.value === v} onChange={() => onChange({ value: v })} />
              {v}
            </label>
          ))}
        </div>
        <div className="mt-1 flex justify-between text-xs text-slate-500">
          <span>{t('0 = Not at all likely')}</span>
          <span>{t('10 = Extremely likely')}</span>
        </div>
      </fieldset>
    );
  }
  if (q.type === 'single' || q.type === 'multi') {
    const chosen = answer?.choices ?? [];
    return (
      <fieldset>
        {legend}
        {q.type === 'multi' && <p className="mt-1 text-xs text-slate-500">{t('Choose all that apply.')}</p>}
        <div className="mt-3 space-y-2">
          {(q.options ?? []).map((o) => (
            <label key={o.key} className={`flex min-h-[44px] cursor-pointer items-center gap-2 rounded-lg border px-3 py-2 text-sm ${chosen.includes(o.key) ? 'border-sapphire bg-sapphire/10' : 'border-slate-200 dark:border-white/10'}`}>
              <input
                type={q.type === 'single' ? 'radio' : 'checkbox'}
                name={q.key}
                checked={chosen.includes(o.key)}
                onChange={() => onChange({ choices: q.type === 'single' ? [o.key] : chosen.includes(o.key) ? chosen.filter((c) => c !== o.key) : [...chosen, o.key] })}
              />
              {pick(locale, o.labelEn, o.labelAr)}
            </label>
          ))}
        </div>
      </fieldset>
    );
  }
  return (
    <fieldset>
      {legend}
      <textarea className="input mt-3 min-h-[110px] w-full" maxLength={2000} value={answer?.text ?? ''} onChange={(e) => onChange({ text: e.target.value })}
        aria-labelledby={`${q.key}-legend`} aria-describedby={`${q.key}-hint`} data-testid={`answer-${q.key}`} />
      <p id={`${q.key}-hint`} className="mt-1 text-xs text-slate-500">{t('Avoid names or details that could identify you or anyone else. Comments are shown only for groups large enough to protect you.')}</p>
    </fieldset>
  );
}
