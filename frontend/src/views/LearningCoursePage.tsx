'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ArrowLeft, Award, ExternalLink, Lock, PlayCircle, RefreshCw } from 'lucide-react';
import { learningApi, type Attempt, type AttemptResult, type EnrollmentDetail, type LaunchResult } from '../api/learning';
import { StatusChip } from '../components/StatusChip';
import { ErrorBlock, LearningGate, LoadingRows, ProgressBar } from '../components/learning/LearningParts';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { useAppToast } from '../components/ui/AppToast';
import { completionRuleLabel, completionSourceLabel, contentKindLabel, enrollmentStatusLabel, enrollmentTone, learningError, learningErrorCode } from '../lib/learning';

/**
 * One course of the employee's own path (LRN-05/06/07): open the content (launch evidence), then complete it the only
 * way its version allows — acknowledge a lesson/link, pass the assessment (scored on the server), or finish the cmi5
 * unit in its own window. Nothing here can mark a course complete by itself: every completion is the server's answer.
 */
export function LearningCoursePage({ enrollmentId }: { enrollmentId: string }) {
  return (
    <LearningGate>
      <CourseBody enrollmentId={enrollmentId} />
    </LearningGate>
  );
}

function CourseBody({ enrollmentId }: { enrollmentId: string }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [detail, setDetail] = useState<EnrollmentDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [launch, setLaunch] = useState<LaunchResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [acknowledged, setAcknowledged] = useState(false);
  const [attempt, setAttempt] = useState<Attempt | null>(null);
  const [answers, setAnswers] = useState<Record<string, string[]>>({});
  const [result, setResult] = useState<AttemptResult | null>(null);
  const [waitingForUnit, setWaitingForUnit] = useState(false);
  const submitted = useRef(false);
  const name = (en: string, ar: string | null) => (locale === 'ar' && ar ? ar : en);

  const load = useCallback(async () => {
    setError(null);
    try {
      const next = await learningApi.enrollment(enrollmentId);
      setDetail(next);
      return next;
    } catch (e) {
      setError(learningError(e, t));
      return null;
    }
  }, [enrollmentId, t]);
  useEffect(() => { void load(); }, [load]);

  // While a cmi5 unit is open in its own window, check every 5 seconds whether it has reported completion.
  useEffect(() => {
    if (!waitingForUnit) return;
    const timer = window.setInterval(async () => {
      const next = await load();
      if (next?.item.status === 'Completed') setWaitingForUnit(false);
    }, 5000);
    return () => window.clearInterval(timer);
  }, [waitingForUnit, load]);

  const run = async (key: string, work: () => Promise<void>) => {
    setBusy(key);
    try {
      await work();
    } catch (e) {
      toast.error(learningError(e, t));
      if (learningErrorCode(e) === 'enrollment_locked' || learningErrorCode(e) === 'attempts_exhausted') await load();
    } finally {
      setBusy(null);
    }
  };

  const open = () => run('open', async () => {
    const res = await learningApi.launch(enrollmentId);
    setLaunch(res);
    setDetail(res.enrollment);
  });

  const openUnit = () => {
    // Open the window in the click itself (a window opened after an await is blocked as a pop-up), then send it to the
    // unit once the server has issued the launch. The unit never runs inside KynexOne's own page (cmi5 OwnWindow).
    const unitWindow = window.open('', '_blank');
    if (unitWindow) unitWindow.opener = null;
    void run('open', async () => {
      try {
        const res = await learningApi.launch(enrollmentId, `${window.location.origin}/ess/learning/${enrollmentId}`);
        setLaunch(res);
        setDetail(res.enrollment);
        if (res.launchUrl && unitWindow) unitWindow.location.href = res.launchUrl;
        else if (res.launchUrl) window.location.href = res.launchUrl;
        setWaitingForUnit(res.enrollment.item.status !== 'Completed');
      } catch (e) {
        unitWindow?.close();
        throw e;
      }
    });
  };

  const acknowledge = () => run('ack', async () => {
    const res = await learningApi.acknowledge(enrollmentId);
    setDetail(res);
    toast.success(t('Course completed. It is on your transcript.'));
  });

  const startAttempt = () => run('attempt', async () => {
    const res = await learningApi.startAttempt(enrollmentId);
    setAttempt(res);
    setAnswers({});
    setResult(null);
    submitted.current = false;
  });

  const submit = () => {
    if (!attempt || submitted.current) return;
    submitted.current = true;
    void run('submit', async () => {
      try {
        const res = await learningApi.submitAttempt(attempt.id, answers);
        setResult(res);
        setAttempt(null);
        await load();
      } catch (e) {
        submitted.current = false;
        throw e;
      }
    });
  };

  if (error) return <ErrorBlock message={error} onRetry={() => void load()} />;
  if (!detail) return <LoadingRows label={t('Loading the course')} />;

  const item = detail.item;
  const done = item.status === 'Completed';
  const locked = item.status === 'Locked';
  const pending = item.status === 'PendingVerification';
  const opened = !!item.firstLaunchedAtUtc || !!launch;
  const attemptsLeft = Math.max(0, item.attemptAllowance - item.attemptsUsed);
  const answeredAll = attempt ? attempt.questions.every((q) => (answers[q.id] ?? []).length > 0) : false;
  const actionable = !done && !locked && !pending && !item.isBlocked && item.nextAction !== 'closed';

  return (
    <div className="mx-auto max-w-3xl space-y-4" data-testid="learning-course">
      <Link href="/ess/learning" className="inline-flex items-center gap-1 text-xs font-semibold text-sapphire hover:underline dark:text-cyanAccent">
        <ArrowLeft className="h-3.5 w-3.5 rtl:rotate-180" aria-hidden="true" />{t('Back to my learning')}
      </Link>

      <header className="wg-card space-y-2 rounded-2xl p-4">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="min-w-0">
            <h1 className="text-lg font-semibold text-slate-800 dark:text-slate-100">{name(item.title, item.titleAr)}</h1>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              {t('{path} · {kind} · version {version}', { path: name(detail.pathName, detail.pathNameAr), kind: t(contentKindLabel[item.contentKind]), version: item.courseVersion })}
            </p>
          </div>
          <StatusChip label={t(enrollmentStatusLabel[item.status])} tone={enrollmentTone(item.status)} dot />
        </div>
        {(detail.description || detail.descriptionAr) && (
          <p className="text-sm text-slate-600 dark:text-slate-300">{name(detail.description ?? '', detail.descriptionAr)}</p>
        )}
        <p className="text-xs text-slate-600 dark:text-slate-300" data-testid="completion-rule">
          <span className="font-semibold">{t('How to complete it: {rule}', { rule: t(completionRuleLabel[item.completionRule]) })}</span>
          {item.completionRule === 'Assessment' && detail.passMarkPercent != null && (
            <span className="ms-2">{t('Pass mark {mark} · {count, plural, one {# attempt left} other {# attempts left}}', { mark: fx.percent(detail.passMarkPercent), count: attemptsLeft })}</span>
          )}
        </p>
        {item.contentKind === 'Cmi5' && item.progressPercent != null && !done && (
          <ProgressBar percent={item.progressPercent} label={t('Progress reported by the learning unit')} />
        )}
      </header>

      {item.isBlocked && (
        <div role="status" className="flex items-start gap-2 rounded-xl bg-slate-100 p-3 text-sm text-slate-700 dark:bg-white/[0.05] dark:text-slate-200">
          <Lock className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          {t('Finish "{course}" first. This course opens when it is complete.', { course: name(item.blockedByTitle ?? '', item.blockedByTitleAr) })}
        </div>
      )}

      {locked && (
        <div role="alert" className="flex items-start gap-2 rounded-xl border border-rose-200 bg-rose-50 p-3 text-sm text-rose-800 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-200" data-testid="course-locked">
          <Lock className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          {t('You have used every attempt for this assessment. HR has been told and can reset your attempts.')}
        </div>
      )}

      {pending && (
        <div role="status" className="rounded-xl border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-100" data-testid="course-pending-verification">
          {t('The course content reported this as complete on {date}. HR checks it before your certificate is issued — you do not need to do anything.', { date: fx.date(item.completedAtUtc) })}
        </div>
      )}

      {done && (
        <div role="status" className="flex flex-wrap items-center justify-between gap-2 rounded-xl border border-emerald-200 bg-emerald-50 p-3 text-sm text-emerald-800 dark:border-emerald-500/20 dark:bg-emerald-500/10 dark:text-emerald-200" data-testid="course-completed">
          <span>
            {t('Completed on {date}', { date: fx.date(item.completedAtUtc) })}
            {item.completionSource && ` · ${t(completionSourceLabel[item.completionSource])}`}
            {item.scorePercent != null && ` · ${t('Score {score}', { score: fx.percent(item.scorePercent) })}`}
          </span>
          {detail.credentialId && (
            <Link href={`/ess/learning/certificates/${detail.credentialId}`} className="btn-secondary inline-flex items-center gap-1 text-xs">
              <Award className="h-3.5 w-3.5" aria-hidden="true" />{t('View certificate')}
            </Link>
          )}
        </div>
      )}

      {/* Content */}
      {!item.isBlocked && !pending && item.nextAction !== 'closed' && (
        <section aria-labelledby="course-content" className="wg-card space-y-3 rounded-2xl p-4">
          <h2 id="course-content" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Course content')}</h2>
          {item.contentKind === 'Lesson' && (launch?.lessonHtml
            ? <div dir="auto" className="learning-lesson space-y-2 text-sm leading-relaxed text-slate-700 dark:text-slate-200 [&_a]:text-sapphire [&_a]:underline [&_h2]:text-base [&_h2]:font-semibold [&_h3]:font-semibold [&_li]:ms-5 [&_ol]:list-decimal [&_ul]:list-disc"
                // Sanitised on the server when the course was saved (LearningHtmlSanitizer rebuilds an allow-list of tags).
                dangerouslySetInnerHTML={{ __html: launch.lessonHtml }} data-testid="lesson-content" />
            : <button type="button" className="btn-primary inline-flex items-center gap-1.5" onClick={open} disabled={busy !== null}>
                <PlayCircle className="h-4 w-4" aria-hidden="true" />{busy === 'open' ? t('Opening…') : opened ? t('Read the lesson again') : t('Open the lesson')}
              </button>)}
          {item.contentKind === 'Link' && detail.linkUrl && (
            <a href={detail.linkUrl} target="_blank" rel="noopener noreferrer" onClick={() => void open()} className="btn-primary inline-flex items-center gap-1.5">
              <ExternalLink className="h-4 w-4" aria-hidden="true" />{t('Open the material in a new tab')}
            </a>
          )}
          {item.contentKind === 'Cmi5' && (
            <div className="space-y-2">
              <button type="button" className="btn-primary inline-flex items-center gap-1.5" onClick={openUnit} disabled={busy !== null || done} data-testid="open-unit">
                <ExternalLink className="h-4 w-4" aria-hidden="true" />{busy === 'open' ? t('Opening…') : opened ? t('Open the learning unit again') : t('Open the learning unit')}
              </button>
              <p className="text-xs text-slate-500 dark:text-slate-400">{t('The unit opens in its own window. When you finish there, this page updates by itself.')}</p>
              {waitingForUnit && (
                <button type="button" className="btn-secondary inline-flex items-center gap-1.5 text-xs" onClick={() => void load()}>
                  <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />{t('Check my progress now')}
                </button>
              )}
            </div>
          )}
        </section>
      )}

      {/* Acknowledgement */}
      {actionable && item.completionRule === 'Acknowledgement' && (
        <section aria-labelledby="course-ack" className="wg-card space-y-3 rounded-2xl p-4">
          <h2 id="course-ack" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Acknowledge')}</h2>
          {!opened && <p className="text-xs text-slate-500 dark:text-slate-400">{t('Open the course content first.')}</p>}
          <label className="flex items-start gap-2 text-sm text-slate-700 dark:text-slate-200">
            <input type="checkbox" className="mt-0.5" checked={acknowledged} disabled={!opened} onChange={(e) => setAcknowledged(e.target.checked)} />
            {t('I have read and understood this course.')}
          </label>
          <button type="button" className="btn-primary" disabled={!opened || !acknowledged || busy !== null} onClick={acknowledge}>
            {busy === 'ack' ? t('Saving...') : t('Confirm and complete')}
          </button>
        </section>
      )}

      {/* Assessment */}
      {!done && item.completionRule === 'Assessment' && (actionable || result) && (
        <section aria-labelledby="course-assessment" className="wg-card space-y-3 rounded-2xl p-4" data-testid="assessment">
          <h2 id="course-assessment" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Assessment')}</h2>
          {result && (
            <div role="status" className={`rounded-xl p-3 text-sm ${result.status === 'Passed'
              ? 'bg-emerald-50 text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-200'
              : 'bg-amber-50 text-amber-800 dark:bg-amber-500/10 dark:text-amber-200'}`} data-testid="attempt-result">
              <p className="font-semibold">
                {result.status === 'Passed'
                  ? t('Passed with {score}. The pass mark is {mark}.', { score: fx.percent(result.scorePercent), mark: fx.percent(result.passMarkPercent) })
                  : t('Not passed: {score}. The pass mark is {mark}.', { score: fx.percent(result.scorePercent), mark: fx.percent(result.passMarkPercent) })}
              </p>
              <p className="text-xs">{t('{correct} of {total} answers correct.', { correct: result.correct, total: result.total })}</p>
              {result.status === 'Failed' && (
                <p className="text-xs">{result.attemptsRemaining > 0
                  ? t('{count, plural, one {You have # attempt left.} other {You have # attempts left.}}', { count: result.attemptsRemaining })
                  : t('You have used every attempt. HR can reset your attempts.')}</p>
              )}
            </div>
          )}
          {!attempt && actionable && (
            <div className="space-y-2">
              <p className="text-xs text-slate-500 dark:text-slate-400">
                {t('{count, plural, one {# question} other {# questions}}. Starting uses one attempt; you can come back to an attempt you started.', { count: detail.questionCount })}
              </p>
              <button type="button" className="btn-primary" onClick={startAttempt} disabled={busy !== null || attemptsLeft === 0 && !detail.openAttemptId} data-testid="start-attempt">
                {busy === 'attempt' ? t('Starting…') : detail.openAttemptId ? t('Continue my attempt') : result ? t('Try again') : t('Start the assessment')}
              </button>
            </div>
          )}
          {attempt && (
            <form className="space-y-4" onSubmit={(e) => { e.preventDefault(); submit(); }} data-testid="attempt-form">
              <p className="text-xs text-slate-500 dark:text-slate-400">{t('Attempt {n} of {max}', { n: attempt.attemptNo, max: attempt.attemptAllowance })}</p>
              {attempt.questions.map((q, index) => (
                <fieldset key={q.id} className="space-y-2 rounded-xl border border-slate-200 p-3 dark:border-white/10">
                  <legend className="px-1 text-sm font-semibold text-slate-800 dark:text-slate-100">
                    {t('{n}. {question}', { n: index + 1, question: name(q.prompt, q.promptAr) })}
                  </legend>
                  {q.kind === 'Multi' && <p className="text-caption text-slate-500">{t('Choose every correct answer.')}</p>}
                  {q.options.map((o) => {
                    const chosen = answers[q.id] ?? [];
                    const multi = q.kind === 'Multi';
                    const label = q.kind === 'TrueFalse' ? (o.id === 'true' ? t('True') : t('False')) : name(o.text, o.textAr);
                    return (
                      <label key={o.id} className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-200">
                        <input
                          type={multi ? 'checkbox' : 'radio'}
                          name={q.id}
                          value={o.id}
                          checked={chosen.includes(o.id)}
                          onChange={(e) => setAnswers((prev) => ({
                            ...prev,
                            [q.id]: multi ? (e.target.checked ? [...chosen, o.id] : chosen.filter((x) => x !== o.id)) : [o.id],
                          }))}
                        />
                        {label}
                      </label>
                    );
                  })}
                </fieldset>
              ))}
              {!answeredAll && <p className="text-xs text-slate-500">{t('Answer every question to submit.')}</p>}
              <button type="submit" className="btn-primary" disabled={!answeredAll || busy !== null} data-testid="submit-attempt">
                {busy === 'submit' ? t('Checking your answers…') : t('Submit answers')}
              </button>
            </form>
          )}
          {detail.attempts.filter((a) => a.status !== 'InProgress').length > 0 && (
            <ul className="space-y-1 text-xs text-slate-500 dark:text-slate-400" aria-label={t('Previous attempts')}>
              {detail.attempts.filter((a) => a.status !== 'InProgress').map((a) => (
                <li key={a.id}>{a.status === 'Passed'
                  ? t('Attempt {n}: {score}, passed', { n: a.attemptNo, score: fx.percent(a.scorePercent ?? 0) })
                  : t('Attempt {n}: {score}, not passed', { n: a.attemptNo, score: fx.percent(a.scorePercent ?? 0) })}</li>
              ))}
            </ul>
          )}
        </section>
      )}
    </div>
  );
}
